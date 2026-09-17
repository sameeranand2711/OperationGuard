using OperationGuard.Testing.Contracts.Drivers;
using OperationGuard.Testing.Contracts.Models;
using Xunit;

namespace OperationGuard.Testing.Contracts.Suites;

public abstract class CoreIdentityAndConfigurationContract<TFactory>
    where TFactory : ICoreContractDriverFactory, new()
{
    private readonly ICoreContractDriver _driver = new TFactory().CreateDriver();

    [Fact]
    public void Identity_uses_scope_operation_name_and_key()
    {
        var baseline = new ContractIdentity("tenant-a", "Payments.Create", "same-key");

        Assert.NotEqual(
            _driver.ComputeIdentityStorageKey(baseline),
            _driver.ComputeIdentityStorageKey(baseline with { Scope = "tenant-b" }));
        Assert.NotEqual(
            _driver.ComputeIdentityStorageKey(baseline),
            _driver.ComputeIdentityStorageKey(baseline with { OperationName = "Wallet.Withdraw" }));
        Assert.NotEqual(
            _driver.ComputeIdentityStorageKey(baseline),
            _driver.ComputeIdentityStorageKey(baseline with { IdempotencyKey = "another-key" }));
    }

    [Fact]
    public void Identity_has_exact_ordinal_semantics()
    {
        var lower = new ContractIdentity("tenant", "Payments.Create", "abc");
        var upper = lower with { IdempotencyKey = "ABC" };

        Assert.NotEqual(
            _driver.ComputeIdentityStorageKey(lower),
            _driver.ComputeIdentityStorageKey(upper));
    }

    [Theory]
    [InlineData("", "Payments.Create", "key")]
    [InlineData("tenant", "", "key")]
    [InlineData("tenant", "Payments.Create", "")]
    public void Identity_rejects_empty_components(string scope, string operationName, string key)
    {
        Assert.ThrowsAny<ArgumentException>(() => _driver.CreateIdentity(scope, operationName, key));
    }

    [Theory]
    [InlineData("bad\rkey")]
    [InlineData("bad\nkey")]
    [InlineData("bad\0key")]
    [InlineData("bad\u001fkey")]
    [InlineData("bad\u007fkey")]
    public void Key_rejects_control_characters(string key)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            _driver.CreateIdentity("tenant", "Payments.Create", key));
    }

    [Fact]
    public void Default_key_length_accepts_255_and_rejects_256()
    {
        _driver.CreateIdentity("tenant", "Payments.Create", new string('k', 255));

        Assert.ThrowsAny<ArgumentException>(() =>
            _driver.CreateIdentity("tenant", "Payments.Create", new string('k', 256)));
    }

    [Fact]
    public void Hard_key_maximum_is_1024()
    {
        _driver.ValidateOptions(new ContractOptions(MaximumKeyLength: 1024));

        Assert.ThrowsAny<ArgumentException>(() =>
            _driver.ValidateOptions(new ContractOptions(MaximumKeyLength: 1025)));
    }

    [Fact]
    public void Replay_limit_default_and_hard_ceiling_are_enforced()
    {
        _driver.ValidateOptions(new ContractOptions(ReplayBodyLimitBytes: 64 * 1024));
        _driver.ValidateOptions(new ContractOptions(ReplayBodyLimitBytes: 1024 * 1024));

        Assert.ThrowsAny<ArgumentException>(() =>
            _driver.ValidateOptions(new ContractOptions(ReplayBodyLimitBytes: 1024 * 1024 + 1)));
    }

    [Fact]
    public void Fingerprint_body_limit_default_and_hard_ceiling_are_enforced()
    {
        _driver.ValidateOptions(new ContractOptions(FingerprintBodyLimitBytes: 1024 * 1024));
        _driver.ValidateOptions(new ContractOptions(FingerprintBodyLimitBytes: 16 * 1024 * 1024));

        Assert.ThrowsAny<ArgumentException>(() =>
            _driver.ValidateOptions(new ContractOptions(FingerprintBodyLimitBytes: 16 * 1024 * 1024 + 1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Retention_and_stale_threshold_must_be_positive(int ticks)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            _driver.ValidateOptions(new ContractOptions(CompletedRetention: TimeSpan.FromTicks(ticks))));
        Assert.ThrowsAny<ArgumentException>(() =>
            _driver.ValidateOptions(new ContractOptions(InProgressStaleAfter: TimeSpan.FromTicks(ticks))));
    }
}
