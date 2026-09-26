using OperationGuard.Testing.Contracts.Suites;
using Xunit;

namespace OperationGuard.SqlServer.IntegrationTests;

public sealed class ProviderContractActivationTests
{
    [Fact]
    public void SqlServer_project_references_the_shared_provider_contract()
    {
        Assert.True(typeof(StoreProviderContract<>).IsGenericTypeDefinition);
        Assert.True(typeof(IdentityCollisionStoreContract<>).IsGenericTypeDefinition);
        Assert.True(typeof(ReplayPersistenceContract<>).IsGenericTypeDefinition);
        Assert.True(typeof(ProviderTransactionFaultContract<>).IsGenericTypeDefinition);
        AssertReplayPersistenceMethods();
    }

    private static void AssertReplayPersistenceMethods()
    {
        var methods = typeof(ReplayPersistenceContract<>).GetMethods()
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("Direct_completion_accepts_exact_header_count_value_and_serialized_boundaries", methods);
        Assert.Contains("Direct_completion_atomically_rejects_each_replay_persistence_violation", methods);
        Assert.Contains("Transactional_completion_atomically_rejects_each_replay_persistence_violation", methods);
        Assert.Contains("Resolve_indeterminate_atomically_rejects_each_replay_persistence_violation", methods);
        Assert.Contains("Direct_completion_accepts_safe_final_status_code_boundaries", methods);
    }
}
