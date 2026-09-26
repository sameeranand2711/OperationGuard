using OperationGuard.Testing.Contracts.Models;

namespace OperationGuard.Testing.Contracts.Drivers;

public interface IContractFingerprintProvider
{
    ValueTask<ContractFingerprint> CreateAsync(
        ContractHttpRequest request,
        CancellationToken cancellationToken);
}
