using OperationGuard.Testing.Contracts.Suites;

namespace OperationGuard.SqlServer.IntegrationTests;

public sealed class SqlServerTransactionFaultContractTests
    : ProviderTransactionFaultContract<SqlServerStoreContractDriverFactory>;
