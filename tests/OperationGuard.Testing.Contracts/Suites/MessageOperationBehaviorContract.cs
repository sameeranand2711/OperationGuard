using OperationGuard.Testing.Contracts.Drivers;
using OperationGuard.Testing.Contracts.Models;
using Xunit;

namespace OperationGuard.Testing.Contracts.Suites;

public abstract class MessageOperationBehaviorContract<TFactory>
    where TFactory : IMessageContractDriverFactory, new()
{
    private static readonly ContractIdentity Identity = new("tenant-a", "ProviderCallback.Settle", "message-1");
    private static readonly ContractFingerprint Fingerprint = ContractFingerprint.FromMarker("payload-a");

    [Fact]
    public async Task Completed_message_duplicate_is_acknowledged_without_reexecution()
    {
        await using var driver = await CreateResetDriverAsync();

        var first = await driver.ExecuteAsync(Identity, Fingerprint, false, CancellationToken.None);
        var duplicate = await driver.ExecuteAsync(Identity, Fingerprint, false, CancellationToken.None);

        Assert.Equal(ContractBeginKind.Acquired, first);
        Assert.Equal(ContractBeginKind.Completed, duplicate);
        Assert.Equal(1, driver.HandlerInvocationCount);
    }

    [Fact]
    public async Task Reused_message_id_with_different_content_never_executes()
    {
        await using var driver = await CreateResetDriverAsync();
        await driver.ExecuteAsync(Identity, Fingerprint, false, CancellationToken.None);

        var collision = await driver.ExecuteAsync(
            Identity,
            ContractFingerprint.FromMarker("payload-b"),
            false,
            CancellationToken.None);

        Assert.Equal(ContractBeginKind.FingerprintMismatch, collision);
        Assert.Equal(1, driver.HandlerInvocationCount);
    }

    [Fact]
    public async Task Ambiguous_message_failure_is_indeterminate_and_not_automatically_retried()
    {
        await using var driver = await CreateResetDriverAsync();
        _ = await Record.ExceptionAsync(async () =>
            await driver.ExecuteAsync(Identity, Fingerprint, true, CancellationToken.None));

        var retry = await driver.ExecuteAsync(Identity, Fingerprint, false, CancellationToken.None);
        var stored = await driver.ReadAsync(Identity, CancellationToken.None);

        Assert.Equal(ContractBeginKind.Indeterminate, retry);
        Assert.Equal(ContractOperationState.Indeterminate, stored?.State);
        Assert.Equal(1, driver.HandlerInvocationCount);
    }

    private static async ValueTask<IMessageContractDriver> CreateResetDriverAsync()
    {
        var driver = await new TFactory().CreateDriverAsync(CancellationToken.None);
        await driver.ResetAsync(CancellationToken.None);
        return driver;
    }
}
