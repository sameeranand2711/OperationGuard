namespace OperationGuard.Testing.Contracts.Drivers;

public interface ICoreContractDriverFactory
{
    ICoreContractDriver CreateDriver();
}

public interface IStoreContractDriverFactory
{
    string ProviderName { get; }

    ValueTask<IStoreContractDriver> CreateDriverAsync(CancellationToken cancellationToken);
}

public interface IHttpContractDriverFactory
{
    ValueTask<IHttpContractDriver> CreateDriverAsync(CancellationToken cancellationToken);
}

public interface IMessageContractDriverFactory
{
    ValueTask<IMessageContractDriver> CreateDriverAsync(CancellationToken cancellationToken);
}
