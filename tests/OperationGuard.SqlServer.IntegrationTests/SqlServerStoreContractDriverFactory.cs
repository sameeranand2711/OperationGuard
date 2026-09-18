using Microsoft.Data.SqlClient;
using OperationGuard.SqlServer.Stores;
using OperationGuard.Testing.Contracts.Drivers;
using Xunit.Sdk;

namespace OperationGuard.SqlServer.IntegrationTests;

public sealed class SqlServerStoreContractDriverFactory : IStoreContractDriverFactory
{
    public string ProviderName => "SQL Server";

    public ValueTask<IStoreContractDriver> CreateDriverAsync(CancellationToken cancellationToken)
    {
        var connectionString = Environment.GetEnvironmentVariable("OPERATIONGUARD_SQLSERVER_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("Set OPERATIONGUARD_SQLSERVER_CONNECTION_STRING to run SQL Server contracts.");
        }

        SqlConnection CreateConnection() => new(connectionString);
        var store = new SqlServerOperationStore(CreateConnection);
        return ValueTask.FromResult<IStoreContractDriver>(new RelationalStoreContractDriver(
            store,
            CreateConnection,
            store.EnsureCreatedAsync,
            """
                IF OBJECT_ID(N'dbo.OperationGuardBusinessMutations', N'U') IS NULL
                    CREATE TABLE dbo.OperationGuardBusinessMutations
                    (
                        Id bigint IDENTITY(1,1) PRIMARY KEY,
                        BusinessKey nvarchar(512) NOT NULL
                    );
                DELETE FROM dbo.OperationGuardBusinessMutations;
                DELETE FROM dbo.OperationGuardOperations;
                """,
            "INSERT INTO dbo.OperationGuardBusinessMutations (BusinessKey) VALUES (@businessKey)",
            "SELECT COUNT(*) FROM dbo.OperationGuardBusinessMutations WHERE BusinessKey = @businessKey"));
    }
}
