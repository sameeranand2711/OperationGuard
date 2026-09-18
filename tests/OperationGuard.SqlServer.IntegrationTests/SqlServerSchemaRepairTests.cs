using Microsoft.Data.SqlClient;
using OperationGuard.SqlServer.Stores;
using OperationGuard.Testing.Contracts.Suites;
using Xunit;
using Xunit.Sdk;

namespace OperationGuard.SqlServer.IntegrationTests;

public sealed class SqlServerSchemaRepairTests
{
    [ProviderFact]
    public async Task Ensure_created_repairs_a_missing_cleanup_index_on_an_existing_table()
    {
        var connectionString = Environment.GetEnvironmentVariable("OPERATIONGUARD_SQLSERVER_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("Set OPERATIONGUARD_SQLSERVER_CONNECTION_STRING to run SQL Server schema contracts.");
        }

        SqlConnection CreateConnection() => new(connectionString);
        var store = new SqlServerOperationStore(CreateConnection);
        await store.EnsureCreatedAsync();
        await ExecuteAsync(
            CreateConnection,
            "DROP INDEX IF EXISTS IX_OperationGuardOperations_Cleanup ON dbo.OperationGuardOperations;");

        try
        {
            await store.EnsureCreatedAsync();

            Assert.Equal(1, await CountCleanupIndexesAsync(CreateConnection));
        }
        finally
        {
            await ExecuteAsync(
                CreateConnection,
                """
                IF NOT EXISTS
                (
                    SELECT 1
                    FROM sys.indexes
                    WHERE object_id = OBJECT_ID(N'dbo.OperationGuardOperations')
                      AND name = N'IX_OperationGuardOperations_Cleanup'
                )
                    CREATE INDEX IX_OperationGuardOperations_Cleanup
                        ON dbo.OperationGuardOperations(State, RetainUntil);
                """);
        }
    }

    private static async Task ExecuteAsync(Func<SqlConnection> connectionFactory, string sql)
    {
        await using var connection = connectionFactory();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountCleanupIndexesAsync(Func<SqlConnection> connectionFactory)
    {
        await using var connection = connectionFactory();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'dbo.OperationGuardOperations')
              AND name = N'IX_OperationGuardOperations_Cleanup';
            """;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
