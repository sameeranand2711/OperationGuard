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
    }
}
