using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics.Metrics;
using OperationGuard.Core.Abstractions;
using OperationGuard.Core.Diagnostics;
using OperationGuard.Core.Internal;
using OperationGuard.Core.Models;

namespace OperationGuard.Core.Execution;

public sealed class MessageOperationExecutor
{
    private static readonly Counter<long> Executions =
        OperationGuardTelemetry.Meter.CreateCounter<long>("operationguard.message.executions");

    private readonly IOperationStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly OperationGuardOptions _options;
    private readonly ILogger<MessageOperationExecutor> _logger;

    public MessageOperationExecutor(
        IOperationStore store,
        OperationGuardOptions? options = null,
        TimeProvider? timeProvider = null,
        ILogger<MessageOperationExecutor>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new OperationGuardOptions();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<MessageOperationExecutor>.Instance;
    }

    public async ValueTask<OperationBeginKind> ExecuteAsync(
        OperationIdentity identity,
        OperationFingerprint fingerprint,
        Func<CancellationToken, ValueTask> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        using var activity = OperationGuardTelemetry.ActivitySource.StartActivity("message.execute");
        activity?.SetTag("operation.name", identity.OperationName);
        var scopeHash = SensitiveValueRedactor.HashForDiagnostics(identity.Scope);
        activity?.SetTag("operation.scope_hash", scopeHash);

        var now = _timeProvider.GetUtcNow();
        var begin = await _store.TryBeginAsync(
            identity,
            fingerprint,
            now,
            _options.InProgressStaleAfter,
            cancellationToken).ConfigureAwait(false);
        if (begin.Kind != OperationBeginKind.Acquired)
        {
            Executions.Add(1, new KeyValuePair<string, object?>("result", begin.Kind.ToString()));
            return begin.Kind;
        }

        try
        {
            await handler(cancellationToken).ConfigureAwait(false);
            var completed = await _store.CompleteAsync(
                identity,
                begin.OwnerToken!,
                response: null,
                replayBodyAvailable: false,
                responseDigest: null,
                _timeProvider.GetUtcNow().Add(_options.CompletedRetention),
                cancellationToken).ConfigureAwait(false);
            if (completed != ConditionalWriteKind.Applied)
            {
                throw new InvalidOperationException($"Guard completion was rejected with '{completed}'.");
            }

            Executions.Add(1, new KeyValuePair<string, object?>("result", "completed"));
            return OperationBeginKind.Acquired;
        }
        catch (Exception exception)
        {
            ConditionalWriteKind? fallbackResult = null;
            try
            {
                fallbackResult = await _store.MarkIndeterminateAsync(
                    identity,
                    begin.OwnerToken!,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception fallbackException)
            {
                _logger.LogError(
                    fallbackException,
                    "Failed to mark a protected message operation indeterminate for operation {OperationName} in scope hash {ScopeHash}.",
                    identity.OperationName,
                    scopeHash);
            }

            if (fallbackResult == ConditionalWriteKind.Applied)
            {
                _logger.LogWarning(
                    exception,
                    "A protected message operation became indeterminate for operation {OperationName} in scope hash {ScopeHash}.",
                    identity.OperationName,
                    scopeHash);
                Executions.Add(1, new KeyValuePair<string, object?>("result", "indeterminate"));
            }
            else
            {
                _logger.LogError(
                    exception,
                    "A protected message operation failed and its indeterminate fallback returned {FallbackResult} for operation {OperationName} in scope hash {ScopeHash}.",
                    fallbackResult?.ToString() ?? "exception",
                    identity.OperationName,
                    scopeHash);
                Executions.Add(1, new KeyValuePair<string, object?>("result", "indeterminate_fallback_failed"));
            }

            throw;
        }
    }
}
