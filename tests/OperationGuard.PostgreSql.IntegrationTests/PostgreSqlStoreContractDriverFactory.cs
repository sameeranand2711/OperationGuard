using Npgsql;
using OperationGuard.PostgreSql.Stores;
using OperationGuard.Testing.Contracts.Drivers;
using Xunit.Sdk;

namespace OperationGuard.PostgreSql.IntegrationTests;

public sealed class PostgreSqlStoreContractDriverFactory : IStoreContractDriverFactory
{
    public string ProviderName => "PostgreSQL";

    public ValueTask<IStoreContractDriver> CreateDriverAsync(CancellationToken cancellationToken)
    {
        var connectionString = Environment.GetEnvironmentVariable("OPERATIONGUARD_POSTGRESQL_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("Set OPERATIONGUARD_POSTGRESQL_CONNECTION_STRING to run PostgreSQL contracts.");
        }

        NpgsqlConnection CreateConnection() => new(connectionString);
        var store = new PostgreSqlOperationStore(CreateConnection);
        return ValueTask.FromResult<IStoreContractDriver>(new RelationalStoreContractDriver(
            store,
            CreateConnection,
            store.EnsureCreatedAsync,
            """
                CREATE TABLE IF NOT EXISTS operation_guard_business_mutations
                (
                    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    business_key varchar(512) NOT NULL
                );
                DELETE FROM operation_guard_business_mutations;
                DELETE FROM operation_guard_operations;
                """,
            "INSERT INTO operation_guard_business_mutations (business_key) VALUES (@businessKey)",
            "SELECT COUNT(*) FROM operation_guard_business_mutations WHERE business_key = @businessKey"));
    }
}
