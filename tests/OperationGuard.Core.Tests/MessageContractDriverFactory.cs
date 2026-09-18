using OperationGuard.Core.Execution;
using OperationGuard.Core.Models;
using OperationGuard.Core.Testing;
using OperationGuard.Testing.Contracts.Drivers;
using OperationGuard.Testing.Contracts.Models;

namespace OperationGuard.Core.Tests;

public sealed class MessageContractDriverFactory : IMessageContractDriverFactory
{
    public ValueTask<IMessageContractDriver> CreateDriverAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IMessageContractDriver>(new Driver());

    private sealed class Driver : IMessageContractDriver
    {
        private readonly InMemoryOperationStore _store = new();
        private readonly MessageOperationExecutor _executor;

        public Driver()
        {
            _executor = new MessageOperationExecutor(_store);
        }

        public int HandlerInvocationCount { get; private set; }

        public ValueTask ResetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _store.Reset();
            HandlerInvocationCount = 0;
            return ValueTask.CompletedTask;
        }

        public ValueTask<ContractBeginKind> ExecuteAsync(
            ContractIdentity identity,
            ContractFingerprint fingerprint,
            bool throwAfterPossibleSideEffect,
            CancellationToken cancellationToken) => ExecuteCoreAsync(identity, fingerprint, throwAfterPossibleSideEffect, cancellationToken);

        public async ValueTask<ContractStoredOperation?> ReadAsync(
            ContractIdentity identity,
            CancellationToken cancellationToken) =>
            Mapping.ToContract(await _store.ReadOutcomeAsync(Mapping.ToCore(identity), cancellationToken));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async ValueTask<ContractBeginKind> ExecuteCoreAsync(
            ContractIdentity identity,
            ContractFingerprint fingerprint,
            bool throwAfterPossibleSideEffect,
            CancellationToken cancellationToken)
        {
            var result = await _executor.ExecuteAsync(
                Mapping.ToCore(identity),
                Mapping.ToCore(fingerprint),
                _ =>
                {
                    HandlerInvocationCount++;
                    if (throwAfterPossibleSideEffect)
                    {
                        throw new InvalidOperationException("Injected ambiguous failure.");
                    }

                    return ValueTask.CompletedTask;
                },
                cancellationToken);
            return (ContractBeginKind)result;
        }
    }
}
