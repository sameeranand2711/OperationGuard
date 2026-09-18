using OperationGuard.Testing.Contracts.Suites;

namespace OperationGuard.PostgreSql.IntegrationTests;

public sealed class PostgreSqlIdentityCollisionContractTests
    : IdentityCollisionStoreContract<PostgreSqlStoreContractDriverFactory>;

public sealed class PostgreSqlReplayPersistenceContractTests
    : ReplayPersistenceContract<PostgreSqlStoreContractDriverFactory>;

public sealed class PostgreSqlTransactionFaultContractTests
    : ProviderTransactionFaultContract<PostgreSqlStoreContractDriverFactory>;
