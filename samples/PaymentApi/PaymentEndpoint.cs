using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Primitives;
using Npgsql;
using OperationGuard.AspNetCore;
using OperationGuard.Core;
using OperationGuard.Core.Models;
using OperationGuard.PostgreSql.Stores;

namespace PaymentApi;

internal static class PaymentEndpoint
{
    private const string OperationName = "Payments.Create";

    internal static ValueTask<string> ResolveTenantScopeAsync(HttpContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tenant = context.User.Identity?.IsAuthenticated == true
            ? context.User.FindFirst("tenant_id")?.Value
            : null;
        return !string.IsNullOrWhiteSpace(tenant)
            ? ValueTask.FromResult(tenant)
            : ValueTask.FromException<string>(
                new InvalidOperationException("An authenticated tenant_id claim is required to establish payment scope."));
    }

    internal static async Task<IResult> HandleAsync(
        PaymentRequest request,
        HttpContext context,
        PostgreSqlOperationStore store,
        OperationGuardAspNetCoreOptions httpOptions,
        OperationGuardOptions options,
        TimeProvider timeProvider,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!TryReadSingleHeader(context.Request.Headers, httpOptions.IdempotencyKeyHeaderName, out var idempotencyKey))
        {
            return Problem(400, "missing-key", "A single non-empty Idempotency-Key header is required.");
        }

        string scope;
        OperationIdentity identity;
        NormalizedPayment normalized;
        try
        {
            scope = await httpOptions.ScopeResolver(context, cancellationToken);
            identity = OperationIdentity.Create(scope, OperationName, idempotencyKey, options.MaximumKeyLength);
            normalized = Normalize(request);
        }
        catch (ArgumentException exception)
        {
            return Problem(400, "invalid-request", exception.Message);
        }

        // This is an explicit semantic fingerprint over stable business fields. The same
        // normalized values are written below, so equivalent JSON formatting does not alter it.
        var fingerprint = CreateFingerprint(normalized);
        var connectionString = configuration.GetConnectionString("OperationGuard")!;

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var session = store.CreateSession(connection, transaction);
            var now = timeProvider.GetUtcNow();
            var begin = await session.TryBeginAsync(
                identity,
                fingerprint,
                now,
                options.InProgressStaleAfter,
                cancellationToken);

            switch (begin.Kind)
            {
                case OperationBeginKind.FingerprintMismatch:
                    await transaction.RollbackAsync(cancellationToken);
                    return Problem(422, "fingerprint-mismatch", "This idempotency key was already used with different payment fields.");

                case OperationBeginKind.AlreadyInProgress:
                    await transaction.RollbackAsync(cancellationToken);
                    return Problem(409, "in-progress", "This payment is already in progress.");

                case OperationBeginKind.Indeterminate:
                    await transaction.RollbackAsync(cancellationToken);
                    return Problem(409, "indeterminate", "The payment outcome requires reconciliation and will not be retried automatically.");

                case OperationBeginKind.Completed:
                    await transaction.RollbackAsync(cancellationToken);
                    return Replay(context, begin.Operation!);

                case OperationBeginKind.Acquired:
                    break;

                default:
                    throw new InvalidOperationException($"Unknown begin result '{begin.Kind}'.");
            }

            var paymentId = Guid.NewGuid();
            await InsertPaymentAsync(connection, transaction, paymentId, scope, normalized, cancellationToken);

            var body = JsonSerializer.SerializeToUtf8Bytes(
                new PaymentResponse(paymentId, "accepted"),
                PaymentJsonContext.Default.PaymentResponse);
            if (body.Length > options.ReplayBodyLimitBytes)
            {
                throw new InvalidOperationException("The payment response exceeded the configured replay limit.");
            }

            var replay = new ReplayResponse(
                StatusCodes.Status201Created,
                new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Content-Type"] = ["application/json"],
                    ["Location"] = [$"/payments/{paymentId:D}"],
                },
                body);
            var completion = await session.CompleteAsync(
                identity,
                begin.OwnerToken!,
                replay,
                replayBodyAvailable: true,
                responseDigest: Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant(),
                now.Add(options.CompletedRetention),
                cancellationToken);
            if (completion != ConditionalWriteKind.Applied)
            {
                throw new InvalidOperationException($"OperationGuard completion was rejected with '{completion}'.");
            }

            // The payment row and Completed guard state become visible in this same commit.
            await transaction.CommitAsync(cancellationToken);
            context.Response.StatusCode = StatusCodes.Status201Created;
            context.Response.Headers.Location = $"/payments/{paymentId:D}";
            return Results.Bytes(body, "application/json");
        }
        catch (NpgsqlException)
        {
            // Fail closed: no unguarded payment handler is run when PostgreSQL is unavailable.
            return Problem(503, "store-unavailable", "Payment protection is temporarily unavailable.");
        }
    }

    private static async Task InsertPaymentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid paymentId,
        string scope,
        NormalizedPayment payment,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sample_payments (payment_id, tenant_scope, account_id, amount, currency)
            VALUES (@paymentId, @scope, @accountId, @amount, @currency)
            """;
        command.Parameters.AddWithValue("paymentId", paymentId);
        command.Parameters.AddWithValue("scope", scope);
        command.Parameters.AddWithValue("accountId", payment.AccountId);
        command.Parameters.AddWithValue("amount", payment.Amount);
        command.Parameters.AddWithValue("currency", payment.Currency);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IResult Replay(HttpContext context, StoredOperation operation)
    {
        if (!operation.ReplayBodyAvailable || operation.Response?.Body is null)
        {
            return Problem(409, "replay-unavailable", "The payment completed, but its stored response body is unavailable.");
        }

        foreach (var header in operation.Response.Headers)
        {
            context.Response.Headers[header.Key] = new StringValues(header.Value);
        }

        context.Response.StatusCode = operation.Response.StatusCode;
        var contentType = operation.Response.Headers.TryGetValue("Content-Type", out var values)
            ? values.SingleOrDefault()
            : null;
        return Results.Bytes(operation.Response.Body, contentType ?? "application/json");
    }

    private static NormalizedPayment Normalize(PaymentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AccountId))
        {
            throw new ArgumentException("AccountId is required.");
        }

        if (request.Amount <= 0)
        {
            throw new ArgumentException("Amount must be positive.");
        }

        var currency = request.Currency?.Trim().ToUpperInvariant();
        if (currency is null || currency.Length != 3 || currency.Any(character => character is < 'A' or > 'Z'))
        {
            throw new ArgumentException("Currency must be a three-letter ASCII code.");
        }

        return new NormalizedPayment(request.AccountId.Trim(), request.Amount, currency);
    }

    private static OperationFingerprint CreateFingerprint(NormalizedPayment payment)
    {
        var canonical = string.Join(
            '\n',
            "payment-fields-v1",
            payment.AccountId,
            payment.Amount.ToString("G29", CultureInfo.InvariantCulture),
            payment.Currency);
        return OperationFingerprint.Sha256(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant());
    }

    private static bool TryReadSingleHeader(IHeaderDictionary headers, string name, out string value)
    {
        StringValues values = headers[name];
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
        {
            value = string.Empty;
            return false;
        }

        value = values[0]!;
        return true;
    }

    private static IResult Problem(int statusCode, string type, string detail) => Results.Problem(
        detail: detail,
        statusCode: statusCode,
        title: type.Replace('-', ' '),
        type: $"urn:operationguard:{type}");
}
