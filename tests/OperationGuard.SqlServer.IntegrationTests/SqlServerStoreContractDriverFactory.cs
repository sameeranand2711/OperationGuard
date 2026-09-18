using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;
using OperationGuard.Core;
using OperationGuard.SqlServer.Stores;
using OperationGuard.Testing.Contracts.Drivers;
using OperationGuard.Testing.Contracts.Models;
using Xunit.Sdk;

namespace OperationGuard.SqlServer.IntegrationTests;

public sealed class SqlServerStoreContractDriverFactory : IStoreContractDriverFactory
{
    public string ProviderName => "SQL Server";

    public ValueTask<IStoreContractDriver> CreateDriverAsync(CancellationToken cancellationToken) =>
        CreateDriverAsync(new ContractOptions(), cancellationToken);

    public ValueTask<IStoreContractDriver> CreateDriverAsync(
        ContractOptions options,
        CancellationToken cancellationToken)
    {
        var connectionString = Environment.GetEnvironmentVariable("OPERATIONGUARD_SQLSERVER_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("Set OPERATIONGUARD_SQLSERVER_CONNECTION_STRING to run SQL Server contracts.");
        }

        SqlConnection CreateConnection() => new(connectionString);
        Func<DbConnection> connectionFactory = CreateConnection;
        var store = CreateStore(connectionFactory, options);
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
            "SELECT COUNT(*) FROM dbo.OperationGuardBusinessMutations WHERE BusinessKey = @businessKey",
            "UPDATE dbo.OperationGuardOperations SET Scope = @scope, OperationName = @operationName, IdempotencyKey = @idempotencyKey",
            (action, commit, token) => ExecuteEfCoreTransactionAsync(
                connectionString,
                action,
                commit,
                token)));
    }

    private static SqlServerOperationStore CreateStore(
        Func<DbConnection> connectionFactory,
        ContractOptions options)
    {
        var coreOptions = new OperationGuardOptions
        {
            MaximumKeyLength = options.MaximumKeyLength,
            FingerprintBodyLimitBytes = options.FingerprintBodyLimitBytes,
            ReplayBodyLimitBytes = options.ReplayBodyLimitBytes,
            CompletedRetention = options.EffectiveCompletedRetention,
            InProgressStaleAfter = options.EffectiveInProgressStaleAfter,
        };
        var configured = typeof(SqlServerOperationStore).GetConstructor(
            [typeof(Func<DbConnection>), typeof(OperationGuardOptions)]);
        return configured is null
            ? new SqlServerOperationStore(connectionFactory)
            : (SqlServerOperationStore)configured.Invoke([connectionFactory, coreOptions]);
    }

    private static async ValueTask ExecuteEfCoreTransactionAsync(
        string connectionString,
        Func<System.Data.Common.DbConnection, System.Data.Common.DbTransaction, CancellationToken, ValueTask> action,
        bool commit,
        CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<ContractDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        await using var context = new ContractDbContext(options);
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await action(
            context.Database.GetDbConnection(),
            transaction.GetDbTransaction(),
            cancellationToken);
        if (commit)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            await transaction.RollbackAsync(cancellationToken);
        }
    }

    private sealed class ContractDbContext(DbContextOptions<ContractDbContext> options) : DbContext(options);
}
