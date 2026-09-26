using OperationGuard.Core.Models;

namespace OperationGuard.Core.Abstractions;

public interface ITransactionalOperationStoreSession
{
    ValueTask<OperationBeginResult> TryBeginAsync(
        OperationIdentity identity,
        OperationFingerprint fingerprint,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    ValueTask<ConditionalWriteKind> CompleteAsync(
        OperationIdentity identity,
        string ownerToken,
        ReplayResponse? response,
        bool replayBodyAvailable,
        string? responseDigest,
        DateTimeOffset retainUntil,
        CancellationToken cancellationToken = default);

    ValueTask<ConditionalWriteKind> MarkIndeterminateAsync(
        OperationIdentity identity,
        string ownerToken,
        CancellationToken cancellationToken = default);
}
