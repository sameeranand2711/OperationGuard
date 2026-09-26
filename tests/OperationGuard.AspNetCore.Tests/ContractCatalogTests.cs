using OperationGuard.Testing.Contracts.Suites;
using Xunit;

namespace OperationGuard.AspNetCore.Tests;

public sealed class ContractCatalogTests
{
    [Fact]
    public void Http_contract_contains_frozen_status_replay_failure_and_fingerprint_behaviors()
    {
        var methods = typeof(AspNetCoreBehaviorContract<>).GetMethods()
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("Protected_endpoint_requires_idempotency_key", methods);
        Assert.Contains("Active_duplicate_returns_409_without_executing_handler", methods);
        Assert.Contains("Same_identity_with_different_raw_body_returns_422_without_reexecution", methods);
        Assert.Contains("Completed_duplicate_replays_without_reexecuting_handler", methods);
        Assert.Contains("Replay_persists_only_safe_selected_headers", methods);
        Assert.Contains("Oversized_response_is_completed_without_body_and_never_reexecutes", methods);
        Assert.Contains("Ambiguous_handler_exception_becomes_indeterminate_not_retryable", methods);
        Assert.Contains("Store_unavailability_fails_closed_without_invoking_handler", methods);
        Assert.Contains("Reversed_repeated_query_values_return_422_without_reexecution", methods);
    }

    [Fact]
    public void Hosted_and_defensive_replay_contracts_cover_pipeline_boundaries()
    {
        var hostedMethods = typeof(HostedPipelineContractTests).GetMethods()
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);
        var replayMethods = typeof(DefensiveReplayTests).GetMethods()
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(
            "Hosted_minimal_endpoint_uses_routing_metadata_and_authentication_before_reservation",
            hostedMethods);
        Assert.Contains(
            "Hosted_mvc_attribute_uses_the_same_guard_pipeline_and_replays_without_reexecution",
            hostedMethods);
        Assert.Contains(
            "Middleware_before_routing_has_no_endpoint_metadata_and_is_an_unsupported_order",
            hostedMethods);
        Assert.Contains(
            "Response_started_before_guard_never_allows_a_missing_key_request_to_reach_the_handler",
            hostedMethods);
        Assert.Contains(
            "Hostile_store_replay_rejects_all_oversized_or_corrupt_headers_before_adding_any_header",
            replayMethods);
        Assert.Contains(
            "Hostile_store_replay_rejects_non_final_or_out_of_range_status_before_adding_headers",
            replayMethods);
        Assert.Contains(
            "Hostile_store_replay_accepts_exact_utf8_and_serialized_header_boundaries",
            replayMethods);
        Assert.Contains("Duplicate_replay_refuses_hostile_persisted_body_above_configured_limit", replayMethods);

        var optionMethods = typeof(UnsafeReplayHeaderConfigurationTests).GetMethods()
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("Replay_header_limits_have_frozen_defaults_in_http_and_core_options", optionMethods);
        Assert.Contains("Replay_header_limits_are_copied_to_core_options", optionMethods);
        Assert.Contains("Replay_header_limit_relationship_is_validated_during_registration", optionMethods);
        Assert.Contains(
            "Replay_header_limits_reject_non_positive_and_above_hard_ceiling_values",
            optionMethods);
    }
}
