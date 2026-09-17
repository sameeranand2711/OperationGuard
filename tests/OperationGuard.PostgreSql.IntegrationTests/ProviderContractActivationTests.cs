using OperationGuard.Testing.Contracts.Suites;
using Xunit;

namespace OperationGuard.PostgreSql.IntegrationTests;

public sealed class ProviderContractActivationTests
{
    [Fact]
    public void PostgreSql_project_references_the_shared_provider_contract()
    {
        Assert.True(typeof(StoreProviderContract<>).IsGenericTypeDefinition);
    }
}
