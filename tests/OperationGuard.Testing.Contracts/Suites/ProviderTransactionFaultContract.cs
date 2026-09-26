using OperationGuard.Testing.Contracts.Drivers;
using OperationGuard.Testing.Contracts.Models;
using Xunit;

namespace OperationGuard.Testing.Contracts.Suites;

public abstract class ProviderTransactionFaultContract<TFactory>
    where TFactory : IStoreContractDriverFactory, new()
{
    private static readonly DateTimeOffset Now = new(2035, 5, 6, 7, 8, 9, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(15);
    private static readonly ContractIdentity Identity = new("tenant-ef", "Payments.Create", "payment-key");
    private static readonly ContractFingerprint Fingerprint = ContractFingerprint.FromMarker("ef-transaction");

    [ProviderFact]
    public async Task EfCore_manual_transaction_rollback_removes_guard_and_business_mutation()
    {
        await using var driver = await CreateResetDriverAsync();

        await driver.ExecuteEfCoreTransactionAsync(async (session, cancellationToken) =>
        {
            var begin = await session.TryBeginAsync(Identity, Fingerprint, Now, Lease, cancellationToken);
            await session.AddBusinessMutationAsync("ef-rollback", cancellationToken);
            await session.CompleteAsync(
                Identity, begin.OwnerToken!, null, false, null, Now.AddHours(24), cancellationToken);
        }, commit: false, CancellationToken.None);

        Assert.Null(await driver.ReadOutcomeAsync(Identity, CancellationToken.None));
        Assert.Equal(0, await driver.CountBusinessMutationsAsync("ef-rollback", CancellationToken.None));
    }

    [ProviderFact]
    public async Task EfCore_manual_transaction_commit_persists_guard_and_business_mutation_together()
    {
        await using var driver = await CreateResetDriverAsync();

        await driver.ExecuteEfCoreTransactionAsync(async (session, cancellationToken) =>
        {
            var begin = await session.TryBeginAsync(Identity, Fingerprint, Now, Lease, cancellationToken);
            await session.AddBusinessMutationAsync("ef-commit", cancellationToken);
            await session.CompleteAsync(
                Identity, begin.OwnerToken!, null, false, null, Now.AddHours(24), cancellationToken);
        }, commit: true, CancellationToken.None);

        Assert.Equal(ContractOperationState.Completed, (await driver.ReadOutcomeAsync(Identity, CancellationToken.None))?.State);
        Assert.Equal(1, await driver.CountBusinessMutationsAsync("ef-commit", CancellationToken.None));
    }

    [ProviderFact]
    public async Task Commit_then_disconnect_reports_ambiguity_but_durable_outcome_remains_completed()
    {
        await using var driver = await CreateResetDriverAsync();

        var exception = await Record.ExceptionAsync(async () =>
            await driver.ExecuteCommitThenDisconnectAsync(
                Identity,
                Fingerprint,
                "commit-then-disconnect",
                Now,
                Lease,
                Now.AddHours(24),
                CancellationToken.None));
        var stored = await driver.ReadOutcomeAsync(Identity, CancellationToken.None);
        var retry = await driver.TryBeginAsync(
            Identity, Fingerprint, Now.AddHours(1), Lease, CancellationToken.None);

        Assert.NotNull(exception);
        Assert.Equal(ContractOperationState.Completed, stored?.State);
        Assert.Equal(1, await driver.CountBusinessMutationsAsync("commit-then-disconnect", CancellationToken.None));
        Assert.Equal(ContractBeginKind.Completed, retry.Kind);
    }

    private static async ValueTask<IStoreContractDriver> CreateResetDriverAsync()
    {
        var driver = await new TFactory().CreateDriverAsync(CancellationToken.None);
        await driver.ResetAsync(CancellationToken.None);
        return driver;
    }
}
