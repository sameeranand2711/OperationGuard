using OperationGuard.Testing.Contracts.Models;

namespace OperationGuard.Testing.Contracts.Drivers;

public interface ITransactionalStoreContractSession
{
    ValueTask<ContractBeginResult> TryBeginAsync(
        ContractIdentity identity,
        ContractFingerprint fingerprint,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    ValueTask AddBusinessMutationAsync(string businessKey, CancellationToken cancellationToken);

    ValueTask<ContractConditionalWriteKind> CompleteAsync(
        ContractIdentity identity,
        string ownerToken,
        ContractReplayResponse? response,
        bool replayBodyAvailable,
        string? responseDigest,
        DateTimeOffset retainUntil,
        CancellationToken cancellationToken);
}
