using OperationGuard.Testing.Contracts.Models;

namespace OperationGuard.Testing.Contracts.Drivers;

public interface IHttpContractDriver : IAsyncDisposable
{
    int HandlerInvocationCount { get; }

    ValueTask ResetAsync(CancellationToken cancellationToken);

    ValueTask<ContractHttpResponse> SendAsync(
        ContractHttpRequest request,
        ContractHandlerResponse handlerResponse,
        CancellationToken cancellationToken);

    ValueTask SeedInProgressAsync(
        ContractHttpRequest request,
        CancellationToken cancellationToken);

    ValueTask<ContractStoredOperation?> ReadStoredOperationAsync(
        ContractIdentity identity,
        CancellationToken cancellationToken);

    ValueTask SetStoreAvailabilityAsync(bool available, CancellationToken cancellationToken);
}
