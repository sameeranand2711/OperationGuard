using OperationGuard.Testing.Contracts.Models;

namespace OperationGuard.Testing.Contracts.Drivers;

public interface IStoreContractDriverFactory
{
    string ProviderName { get; }

    ValueTask<IStoreContractDriver> CreateDriverAsync(CancellationToken cancellationToken);

    ValueTask<IStoreContractDriver> CreateDriverAsync(
        ContractOptions options,
        CancellationToken cancellationToken) =>
        CreateDriverAsync(cancellationToken);
}
