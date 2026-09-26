using System.Data.Common;
using OperationGuard.Core.Models;

namespace OperationGuard.Core.Abstractions;

public interface IOperationStore
{
    ValueTask<OperationBeginResult> TryBeginAsync(
        OperationIdentity identity,
        OperationFingerprint fingerprint,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    ValueTask<StoredOperation?> ReadOutcomeAsync(
        OperationIdentity identity,
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

    ValueTask<ConditionalWriteKind> ResolveIndeterminateAsync(
        OperationIdentity identity,
        long expectedRecoveryVersion,
        ReplayResponse? response,
        DateTimeOffset retainUntil,
        CancellationToken cancellationToken = default);

    ValueTask<OperationBeginResult> AuthorizeRecoveryAttemptAsync(
        OperationIdentity identity,
        long expectedRecoveryVersion,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    ValueTask<CleanupResult> DeleteExpiredBatchAsync(
        DateTimeOffset now,
        int maximumCount,
        CancellationToken cancellationToken = default);

    ITransactionalOperationStoreSession CreateSession(DbConnection connection, DbTransaction transaction);
}
