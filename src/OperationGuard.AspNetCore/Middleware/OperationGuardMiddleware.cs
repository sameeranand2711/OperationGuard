using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using OperationGuard.AspNetCore.Internal;
using OperationGuard.AspNetCore.Metadata;
using OperationGuard.Core;
using OperationGuard.Core.Abstractions;
using OperationGuard.Core.Diagnostics;
using OperationGuard.Core.Internal;
using OperationGuard.Core.Models;

namespace OperationGuard.AspNetCore;

public sealed class OperationGuardMiddleware
{
    private const string ProblemBase = "urn:operationguard:";
    private static readonly Counter<long> Requests =
        OperationGuardTelemetry.Meter.CreateCounter<long>("operationguard.http.requests");

    private readonly RequestDelegate _next;
    private readonly OperationGuardAspNetCoreOptions _options;
    private readonly OperationGuardOptions _coreOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OperationGuardMiddleware> _logger;

    public OperationGuardMiddleware(
        RequestDelegate next,
        OperationGuardAspNetCoreOptions options,
        OperationGuardOptions coreOptions,
        TimeProvider timeProvider,
        ILogger<OperationGuardMiddleware> logger)
    {
        _next = next;
        _options = options;
        _coreOptions = coreOptions;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IOperationStore store,
        IRequestFingerprintProvider fingerprintProvider)
    {
        var endpoint = context.GetEndpoint();
        var metadata = endpoint?.Metadata.GetMetadata<OperationGuardEndpointMetadata>();
        var attribute = endpoint?.Metadata.GetMetadata<OperationGuardAttribute>();
        var operationName = metadata?.OperationName ?? attribute?.OperationName;
        if (operationName is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (!TryReadKey(context.Request.Headers, out var key))
        {
            await WriteProblemAsync(context, 400, "missing-key", "A valid Idempotency-Key header is required.").ConfigureAwait(false);
            return;
        }

        string scope;
        OperationIdentity identity;
        OperationFingerprint fingerprint;
        try
        {
            scope = await _options.ScopeResolver(context, context.RequestAborted).ConfigureAwait(false);
            identity = OperationIdentity.Create(scope, operationName, key, _coreOptions.MaximumKeyLength);
            var body = await ReadRequestBodyAsync(context.Request, _coreOptions.FingerprintBodyLimitBytes, context.RequestAborted).ConfigureAwait(false);
            var selectedHeaders = SelectRequestHeaders(context.Request.Headers);
            fingerprint = await fingerprintProvider.CreateAsync(
                new FingerprintInput(
                    operationName,
                    context.Request.Method,
                    context.Request.QueryString.Value ?? string.Empty,
                    context.Request.ContentType ?? string.Empty,
                    body,
                    selectedHeaders),
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            await WriteProblemAsync(context, 400, "invalid-request", exception.Message).ConfigureAwait(false);
            return;
        }
        catch (IOException exception)
        {
            await WriteProblemAsync(context, 413, "body-too-large", exception.Message).ConfigureAwait(false);
            return;
        }

        OperationBeginResult begin;
        try
        {
            begin = await store.TryBeginAsync(
                identity,
                fingerprint,
                _timeProvider.GetUtcNow(),
                _coreOptions.InProgressStaleAfter,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
        {
            _logger.LogError(
                exception,
                "The operation protection store was unavailable for operation {OperationName}.",
                operationName);
            await WriteProblemAsync(context, 503, "store-unavailable", "Operation protection is temporarily unavailable.").ConfigureAwait(false);
            return;
        }

        switch (begin.Kind)
        {
            case OperationBeginKind.FingerprintMismatch:
                Requests.Add(1, new KeyValuePair<string, object?>("result", "fingerprint_mismatch"));
                await WriteProblemAsync(context, 422, "fingerprint-mismatch", "The idempotency key was already used with a different request.").ConfigureAwait(false);
                return;
            case OperationBeginKind.AlreadyInProgress:
                Requests.Add(1, new KeyValuePair<string, object?>("result", "in_progress"));
                await WriteProblemAsync(context, 409, "in-progress", "The operation is already in progress.").ConfigureAwait(false);
                return;
            case OperationBeginKind.Indeterminate:
                Requests.Add(1, new KeyValuePair<string, object?>("result", "indeterminate"));
                await WriteProblemAsync(context, 409, "indeterminate", "The operation outcome is indeterminate and requires reconciliation.").ConfigureAwait(false);
                return;
            case OperationBeginKind.Completed:
                await ReplayAsync(context, begin.Operation!).ConfigureAwait(false);
                return;
            case OperationBeginKind.Acquired:
                await ExecuteOwnedAsync(context, store, identity, begin.OwnerToken!).ConfigureAwait(false);
                return;
            default:
                throw new InvalidOperationException("Unknown begin result.");
        }
    }

    private async Task ExecuteOwnedAsync(
        HttpContext context,
        IOperationStore store,
        OperationIdentity identity,
        string ownerToken)
    {
        var originalBody = context.Response.Body;
        await using var capture = new BoundedCaptureStream(originalBody, _coreOptions.ReplayBodyLimitBytes);
        context.Response.Body = capture;
        try
        {
            await _next(context).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch
        {
            context.Response.Body = originalBody;
            var fallback = await TryMarkIndeterminateAsync(store, identity, ownerToken).ConfigureAwait(false);
            Requests.Add(
                1,
                new KeyValuePair<string, object?>(
                    "result",
                    fallback == ConditionalWriteKind.Applied
                        ? "indeterminate"
                        : "indeterminate_fallback_failed"));
            throw;
        }

        context.Response.Body = originalBody;
        try
        {
            var response = new ReplayResponse(
                context.Response.StatusCode,
                SelectResponseHeaders(context.Response.Headers),
                capture.CapturedBody);
            var result = await store.CompleteAsync(
                identity,
                ownerToken,
                response,
                capture.BodyAvailable,
                capture.Digest,
                _timeProvider.GetUtcNow().Add(_coreOptions.CompletedRetention),
                context.RequestAborted).ConfigureAwait(false);
            if (result != ConditionalWriteKind.Applied)
            {
                throw new InvalidOperationException($"Guard completion was rejected with '{result}'.");
            }
        }
        catch
        {
            await TryMarkIndeterminateAsync(store, identity, ownerToken).ConfigureAwait(false);
            throw;
        }

        Requests.Add(1, new KeyValuePair<string, object?>("result", "completed"));
    }

    private async Task ReplayAsync(HttpContext context, StoredOperation operation)
    {
        if (!operation.ReplayBodyAvailable
            || operation.Response?.Body is not { } body)
        {
            await WriteProblemAsync(context, 409, "replay-unavailable", "The operation completed, but its response body is unavailable for replay.").ConfigureAwait(false);
            return;
        }

        try
        {
            ReplayPersistenceValidator.ValidateForReplay(operation.Response, _coreOptions);
        }
        catch (ArgumentException)
        {
            await WriteProblemAsync(context, 409, "replay-unavailable", "The operation completed, but its stored response is unsafe for replay.").ConfigureAwait(false);
            return;
        }

        var replayHeaders = operation.Response.Headers
            .Where(header => _options.IsReplayHeaderAllowed(header.Key))
            .ToArray();
        context.Response.StatusCode = operation.Response.StatusCode;
        foreach (var header in replayHeaders)
        {
            context.Response.Headers[header.Key] = new StringValues(header.Value);
        }

        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
        Requests.Add(1, new KeyValuePair<string, object?>("result", "replayed"));
    }

    private async ValueTask<ConditionalWriteKind?> TryMarkIndeterminateAsync(
        IOperationStore store,
        OperationIdentity identity,
        string ownerToken)
    {
        try
        {
            var result = await store.MarkIndeterminateAsync(
                identity,
                ownerToken,
                CancellationToken.None).ConfigureAwait(false);
            if (result != ConditionalWriteKind.Applied)
            {
                _logger.LogError(
                    "The indeterminate fallback returned {FallbackResult} for operation {OperationName}.",
                    result,
                    identity.OperationName);
            }

            return result;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "The indeterminate fallback failed for operation {OperationName}.",
                identity.OperationName);
            return null;
        }
    }

    private bool TryReadKey(IHeaderDictionary headers, out string key)
    {
        var values = headers[_options.IdempotencyKeyHeaderName];
        if (values.Count != 1 || string.IsNullOrEmpty(values[0]))
        {
            key = string.Empty;
            return false;
        }

        key = values[0]!;
        return true;
    }

    private Dictionary<string, string[]> SelectRequestHeaders(IHeaderDictionary headers) =>
        _options.FingerprintHeaders
            .Where(headers.ContainsKey)
            .ToDictionary(
                name => name,
                name => headers[name].Select(value => value!).ToArray(),
                StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, string[]> SelectResponseHeaders(IHeaderDictionary headers) =>
        _options.ReplayHeaders
            .Where(headers.ContainsKey)
            .ToDictionary(
                name => name,
                name => headers[name].Select(value => value!).ToArray(),
                StringComparer.OrdinalIgnoreCase);

    private static async ValueTask<byte[]> ReadRequestBodyAsync(
        HttpRequest request,
        int limit,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > limit)
        {
            throw new IOException($"The request body exceeds the configured fingerprint limit of {limit} bytes.");
        }

        request.EnableBuffering(bufferThreshold: Math.Min(limit, 64 * 1024), bufferLimit: limit + 1L);
        request.Body.Position = 0;
        using var body = new MemoryStream(Math.Min(limit, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (body.Length + read > limit)
            {
                request.Body.Position = 0;
                throw new IOException($"The request body exceeds the configured fingerprint limit of {limit} bytes.");
            }

            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        request.Body.Position = 0;
        return body.ToArray();
    }

    private static Task WriteProblemAsync(HttpContext context, int statusCode, string type, string detail)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        return context.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = statusCode,
                Type = ProblemBase + type,
                Title = type.Replace('-', ' '),
                Detail = detail,
            },
            cancellationToken: context.RequestAborted);
    }
}
