using OperationGuard.Testing.Contracts.Suites;
using Xunit;

namespace OperationGuard.Core.Tests;

public sealed class ContractCatalogTests
{
    [Fact]
    public void Core_contract_contains_all_frozen_behavior_groups()
    {
        AssertMethods(
            typeof(CoreIdentityAndConfigurationContract<>),
            "Identity_uses_scope_operation_name_and_key",
            "Identity_has_exact_ordinal_semantics",
            "Identity_rejects_empty_components",
            "Key_rejects_control_characters",
            "Default_key_length_accepts_255_and_rejects_256",
            "Hard_key_maximum_is_1024",
            "Replay_limit_default_and_hard_ceiling_are_enforced",
            "Fingerprint_body_limit_default_and_hard_ceiling_are_enforced",
            "Retention_and_stale_threshold_must_be_positive");
        AssertMethods(
            typeof(FingerprintingContract<>),
            "Default_digest_is_sha256_and_versioned",
            "Operation_method_content_type_and_selected_headers_are_semantic_inputs",
            "Query_parameter_order_is_canonicalized",
            "Raw_json_property_order_is_not_silently_canonicalized",
            "Default_fingerprint_rejects_body_above_one_mebibyte",
            "Custom_provider_is_used_and_observes_cancellation");
    }

    [Fact]
    public void Message_contract_is_broker_neutral_and_covers_duplicates_conflicts_and_ambiguity()
    {
        AssertMethods(
            typeof(MessageOperationBehaviorContract<>),
            "Completed_message_duplicate_is_acknowledged_without_reexecution",
            "Reused_message_id_with_different_content_never_executes",
            "Ambiguous_message_failure_is_indeterminate_and_not_automatically_retried");
    }

    private static void AssertMethods(Type contract, params string[] names)
    {
        var methods = contract.GetMethods().Select(method => method.Name).ToHashSet(StringComparer.Ordinal);
        Assert.All(names, name => Assert.Contains(name, methods));
    }
}
