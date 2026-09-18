using OperationGuard.Testing.Contracts.Suites;

namespace OperationGuard.SqlServer.IntegrationTests;

public sealed class SqlServerIdentityCollisionContractTests
    : IdentityCollisionStoreContract<SqlServerStoreContractDriverFactory>;

public sealed class SqlServerReplayPersistenceContractTests
    : ReplayPersistenceContract<SqlServerStoreContractDriverFactory>;

public sealed class SqlServerTransactionFaultContractTests
    : ProviderTransactionFaultContract<SqlServerStoreContractDriverFactory>;
