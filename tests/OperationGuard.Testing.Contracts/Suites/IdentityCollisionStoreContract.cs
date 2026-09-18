using OperationGuard.Testing.Contracts.Drivers;
using OperationGuard.Testing.Contracts.Models;
using Xunit;

namespace OperationGuard.Testing.Contracts.Suites;

public abstract class IdentityCollisionStoreContract<TFactory>
    where TFactory : IStoreContractDriverFactory, new()
{
    private static readonly DateTimeOffset Now = new(2035, 3, 4, 5, 6, 7, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(15);
    private static readonly ContractFingerprint Fingerprint = ContractFingerprint.FromMarker("identity-collision");
    private static readonly ContractIdentity Original = new("tenant-a", "Payments.Create", "key");
    private static readonly ContractIdentity Mutated = new("tenant-b", "Wallet.Withdraw", "different-key");

    [ProviderFact]
    public async Task Stored_identity_mutation_is_detected_on_read()
    {
        await using var driver = await CreateResetDriverAsync();
        await SeedAsync(driver);
        await driver.MutateStoredIdentityAsync(Mutated, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await driver.ReadOutcomeAsync(Original, CancellationToken.None));
    }

    [ProviderFact]
    public async Task Stored_identity_mutation_is_detected_on_duplicate_begin()
    {
        await using var driver = await CreateResetDriverAsync();
        await SeedAsync(driver);
        await driver.MutateStoredIdentityAsync(Mutated, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await driver.TryBeginAsync(Original, Fingerprint, Now, Lease, CancellationToken.None));
    }

    [ProviderFact]
    public async Task Stored_identity_mutation_cannot_complete_another_identity_hash_record()
    {
        await using var driver = await CreateResetDriverAsync();
        var begin = await SeedAsync(driver);
        await driver.MutateStoredIdentityAsync(Mutated, CancellationToken.None);

        await AssertRejectedAsync(() => driver.CompleteAsync(
            Original, begin.OwnerToken!, null, false, null, Now.AddHours(24), CancellationToken.None));
    }

    [ProviderFact]
    public async Task Stored_identity_mutation_cannot_mark_another_identity_hash_record_indeterminate()
    {
        await using var driver = await CreateResetDriverAsync();
        var begin = await SeedAsync(driver);
        await driver.MutateStoredIdentityAsync(Mutated, CancellationToken.None);

        await AssertRejectedAsync(() => driver.MarkIndeterminateAsync(
            Original, begin.OwnerToken!, CancellationToken.None));
    }

    [ProviderFact]
    public async Task Stored_identity_mutation_cannot_resolve_another_identity_hash_record()
    {
        await using var driver = await CreateResetDriverAsync();
        var begin = await SeedAsync(driver);
        Assert.Equal(
            ContractConditionalWriteKind.Applied,
            await driver.MarkIndeterminateAsync(Original, begin.OwnerToken!, CancellationToken.None));
        var indeterminate = await driver.ReadOutcomeAsync(Original, CancellationToken.None);
        await driver.MutateStoredIdentityAsync(Mutated, CancellationToken.None);

        await AssertRejectedAsync(() => driver.ResolveIndeterminateAsync(
            Original, indeterminate!.RecoveryVersion, null, Now.AddHours(24), CancellationToken.None));
    }

    [ProviderFact]
    public async Task Stored_identity_mutation_cannot_claim_another_identity_hash_record_for_recovery()
    {
        await using var driver = await CreateResetDriverAsync();
        var begin = await SeedAsync(driver);
        await driver.MarkIndeterminateAsync(Original, begin.OwnerToken!, CancellationToken.None);
        var indeterminate = await driver.ReadOutcomeAsync(Original, CancellationToken.None);
        await driver.MutateStoredIdentityAsync(Mutated, CancellationToken.None);

        ContractBeginResult? result = null;
        var exception = await Record.ExceptionAsync(async () => result =
            await driver.AuthorizeRecoveryAttemptAsync(
                Original,
                indeterminate!.RecoveryVersion,
                Now.AddHours(1),
                Lease,
                CancellationToken.None));

        Assert.True(
            exception is InvalidOperationException || result?.Kind != ContractBeginKind.Acquired,
            "Recovery must not acquire a row whose stored identity does not exactly match the requested identity.");
    }

    private static async Task AssertRejectedAsync(Func<ValueTask<ContractConditionalWriteKind>> action)
    {
        ContractConditionalWriteKind? result = null;
        var exception = await Record.ExceptionAsync(async () => result = await action());
        Assert.True(
            exception is InvalidOperationException || result != ContractConditionalWriteKind.Applied,
            "A conditional mutation must verify the exact stored identity before applying by identity hash.");
    }

    private static async Task<ContractBeginResult> SeedAsync(IStoreContractDriver driver)
    {
        var begin = await driver.TryBeginAsync(Original, Fingerprint, Now, Lease, CancellationToken.None);
        Assert.Equal(ContractBeginKind.Acquired, begin.Kind);
        return begin;
    }

    private static async ValueTask<IStoreContractDriver> CreateResetDriverAsync()
    {
        var driver = await new TFactory().CreateDriverAsync(CancellationToken.None);
        await driver.ResetAsync(CancellationToken.None);
        return driver;
    }
}
