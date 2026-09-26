using OperationGuard.Testing.Contracts.Models;

namespace OperationGuard.Testing.Contracts.Drivers;

public interface IStoreContractDriver : IAsyncDisposable
{
    ValueTask ResetAsync(CancellationToken cancellationToken);

    ValueTask<ContractBeginResult> TryBeginAsync(
        ContractIdentity identity,
        ContractFingerprint fingerprint,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    ValueTask<ContractStoredOperation?> ReadOutcomeAsync(
        ContractIdentity identity,
        CancellationToken cancellationToken);

    ValueTask<ContractConditionalWriteKind> CompleteAsync(
        ContractIdentity identity,
        string ownerToken,
        ContractReplayResponse? response,
        bool replayBodyAvailable,
        string? responseDigest,
        DateTimeOffset retainUntil,
        CancellationToken cancellationToken);

    ValueTask<ContractConditionalWriteKind> MarkIndeterminateAsync(
        ContractIdentity identity,
        string ownerToken,
        CancellationToken cancellationToken);

    ValueTask<ContractConditionalWriteKind> ResolveIndeterminateAsync(
        ContractIdentity identity,
        long expectedRecoveryVersion,
        ContractReplayResponse? response,
        DateTimeOffset retainUntil,
        CancellationToken cancellationToken);

    ValueTask<ContractBeginResult> AuthorizeRecoveryAttemptAsync(
        ContractIdentity identity,
        long expectedRecoveryVersion,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    ValueTask<ContractCleanupResult> DeleteExpiredBatchAsync(
        DateTimeOffset now,
        int maximumCount,
        CancellationToken cancellationToken);

    ValueTask ExecuteTransactionAsync(
        Func<ITransactionalStoreContractSession, CancellationToken, ValueTask> action,
        bool commit,
        CancellationToken cancellationToken);

    ValueTask ExecuteEfCoreTransactionAsync(
        Func<ITransactionalStoreContractSession, CancellationToken, ValueTask> action,
        bool commit,
        CancellationToken cancellationToken);

    ValueTask ExecuteCommitThenDisconnectAsync(
        ContractIdentity identity,
        ContractFingerprint fingerprint,
        string businessKey,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        DateTimeOffset retainUntil,
        CancellationToken cancellationToken);

    ValueTask<int> CountBusinessMutationsAsync(string businessKey, CancellationToken cancellationToken);

    ValueTask MutateStoredIdentityAsync(
        ContractIdentity replacementIdentity,
        CancellationToken cancellationToken);
}
