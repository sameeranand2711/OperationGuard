namespace OperationGuard.Testing.Contracts.Drivers;

public interface IMessageContractDriverFactory
{
    ValueTask<IMessageContractDriver> CreateDriverAsync(CancellationToken cancellationToken);
}
