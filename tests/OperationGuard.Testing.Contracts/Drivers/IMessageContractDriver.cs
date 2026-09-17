using OperationGuard.Testing.Contracts.Models;

namespace OperationGuard.Testing.Contracts.Drivers;

public interface IMessageContractDriver : IAsyncDisposable
{
    int HandlerInvocationCount { get; }

    ValueTask ResetAsync(CancellationToken cancellationToken);

    ValueTask<ContractBeginKind> ExecuteAsync(
        ContractIdentity identity,
        ContractFingerprint fingerprint,
        bool throwAfterPossibleSideEffect,
        CancellationToken cancellationToken);

    ValueTask<ContractStoredOperation?> ReadAsync(
        ContractIdentity identity,
        CancellationToken cancellationToken);
}
