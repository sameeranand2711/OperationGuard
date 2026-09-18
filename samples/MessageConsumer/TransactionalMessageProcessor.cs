using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using OperationGuard.Core;
using OperationGuard.Core.Models;
using OperationGuard.PostgreSql.Stores;

namespace MessageConsumer;

internal sealed class TransactionalMessageProcessor(
    PostgreSqlOperationStore store,
    string connectionString,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    internal async Task<MessageHandlingOutcome> HandleAsync(
        IncomingMessage message,
        CancellationToken cancellationToken)
    {
        var identity = new OperationIdentity(
            message.TenantScope,
            "AccountCredit.Apply",
            message.MessageId);
        var fingerprint = CreateFingerprint(message);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var session = store.CreateSession(connection, transaction);
        var now = timeProvider.GetUtcNow();
        var begin = await session.TryBeginAsync(
            identity,
            fingerprint,
            now,
            LeaseDuration,
            cancellationToken);

        switch (begin.Kind)
        {
            case OperationBeginKind.Completed:
                await transaction.RollbackAsync(cancellationToken);
                return MessageHandlingOutcome.Duplicate;

            case OperationBeginKind.FingerprintMismatch:
                await transaction.RollbackAsync(cancellationToken);
                throw new InvalidOperationException("The message ID was reused with different business content.");

            case OperationBeginKind.AlreadyInProgress:
                await transaction.RollbackAsync(cancellationToken);
                throw new InvalidOperationException("The message is already being processed by another consumer.");

            case OperationBeginKind.Indeterminate:
                await transaction.RollbackAsync(cancellationToken);
                throw new InvalidOperationException("The message outcome is indeterminate and requires reconciliation.");

            case OperationBeginKind.Acquired:
                break;

            default:
                throw new InvalidOperationException($"Unknown begin result '{begin.Kind}'.");
        }

        await InsertBusinessMutationAsync(connection, transaction, message, cancellationToken);
        var completion = await session.CompleteAsync(
            identity,
            begin.OwnerToken!,
            response: null,
            replayBodyAvailable: false,
            responseDigest: null,
            now.Add(Retention),
            cancellationToken);
        if (completion != ConditionalWriteKind.Applied)
        {
            throw new InvalidOperationException($"OperationGuard completion was rejected with '{completion}'.");
        }

        // The business mutation and Completed record commit atomically in one local transaction.
        // No broker transaction or remote side effect is implied by this boundary.
        await transaction.CommitAsync(cancellationToken);
        return MessageHandlingOutcome.Processed;
    }

    private static async Task InsertBusinessMutationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IncomingMessage message,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sample_message_credits (message_id, tenant_scope, account_id, amount)
            VALUES (@messageId, @scope, @accountId, @amount)
            """;
        command.Parameters.AddWithValue("messageId", message.MessageId);
        command.Parameters.AddWithValue("scope", message.TenantScope);
        command.Parameters.AddWithValue("accountId", message.AccountId);
        command.Parameters.AddWithValue("amount", message.Amount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static OperationFingerprint CreateFingerprint(IncomingMessage message)
    {
        var canonical = string.Join(
            '\n',
            "account-credit-v1",
            message.AccountId,
            message.Amount.ToString("G29", CultureInfo.InvariantCulture));
        return OperationFingerprint.Sha256(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant());
    }
}
