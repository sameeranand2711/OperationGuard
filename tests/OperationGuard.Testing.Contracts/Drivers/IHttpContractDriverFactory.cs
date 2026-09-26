namespace OperationGuard.Testing.Contracts.Drivers;

public interface IHttpContractDriverFactory
{
    ValueTask<IHttpContractDriver> CreateDriverAsync(CancellationToken cancellationToken);
}
