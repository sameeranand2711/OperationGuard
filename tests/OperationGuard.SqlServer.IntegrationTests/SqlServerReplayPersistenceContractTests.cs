using OperationGuard.Testing.Contracts.Suites;

namespace OperationGuard.SqlServer.IntegrationTests;

public sealed class SqlServerReplayPersistenceContractTests
    : ReplayPersistenceContract<SqlServerStoreContractDriverFactory>;
