using OperationGuard.Testing.Contracts.Suites;

namespace OperationGuard.PostgreSql.IntegrationTests;

public sealed class PostgreSqlIdentityCollisionContractTests
    : IdentityCollisionStoreContract<PostgreSqlStoreContractDriverFactory>;
