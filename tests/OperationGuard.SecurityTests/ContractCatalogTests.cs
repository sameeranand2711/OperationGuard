using OperationGuard.Testing.Contracts.Suites;
using Xunit;

namespace OperationGuard.SecurityTests;

public sealed class ContractCatalogTests
{
    [Fact]
    public void Contracts_cover_bounded_input_filtered_replay_scope_isolation_and_fail_closed_behavior()
    {
        AssertMethod(typeof(CoreIdentityAndConfigurationContract<>), "Key_rejects_control_characters");
        AssertMethod(typeof(CoreIdentityAndConfigurationContract<>), "Default_key_length_accepts_255_and_rejects_256");
        AssertMethod(typeof(FingerprintingContract<>), "Default_fingerprint_rejects_body_above_one_mebibyte");
        AssertMethod(typeof(AspNetCoreBehaviorContract<>), "Replay_persists_only_safe_selected_headers");
        AssertMethod(typeof(AspNetCoreBehaviorContract<>), "Store_unavailability_fails_closed_without_invoking_handler");
        AssertMethod(typeof(StoreProviderContract<>), "Same_client_key_is_isolated_by_scope_and_operation_name");
    }

    private static void AssertMethod(Type contract, string name)
    {
        Assert.Contains(contract.GetMethods(), method => method.Name == name);
    }
}
