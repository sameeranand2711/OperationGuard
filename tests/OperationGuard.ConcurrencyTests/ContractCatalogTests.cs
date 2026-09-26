using OperationGuard.Testing.Contracts.Suites;
using Xunit;

namespace OperationGuard.ConcurrencyTests;

public sealed class ContractCatalogTests
{
    [Fact]
    public void Shared_provider_contract_contains_deterministic_hot_key_and_fencing_tests()
    {
        var methods = typeof(StoreProviderContract<>).GetMethods()
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("One_hundred_simultaneous_same_key_reservations_have_exactly_one_owner", methods);
        Assert.Contains("Mixed_fingerprint_race_never_acquires_both_payloads", methods);
        Assert.Contains("Stale_owner_cannot_complete_after_an_explicit_recovery_claim", methods);
        Assert.Contains("Cleanup_removes_only_expired_completed_records", methods);
        Assert.Contains("Cleanup_racing_completion_cannot_delete_the_active_operation", methods);
        Assert.Contains("Lease_expiry_alone_does_not_make_an_active_operation_safe_to_retry", methods);
    }
}
