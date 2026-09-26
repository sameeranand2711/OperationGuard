using OperationGuard.Testing.Contracts.Suites;

namespace OperationGuard.PostgreSql.IntegrationTests;

public sealed class PostgreSqlReplayPersistenceContractTests
    : ReplayPersistenceContract<PostgreSqlStoreContractDriverFactory>;
