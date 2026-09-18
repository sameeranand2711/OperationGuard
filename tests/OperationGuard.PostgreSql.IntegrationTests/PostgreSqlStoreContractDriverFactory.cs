using Npgsql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;
using OperationGuard.Core;
using OperationGuard.PostgreSql.Stores;
using OperationGuard.Testing.Contracts.Drivers;
using OperationGuard.Testing.Contracts.Models;
using Xunit.Sdk;

namespace OperationGuard.PostgreSql.IntegrationTests;

public sealed class PostgreSqlStoreContractDriverFactory : IStoreContractDriverFactory
{
    public string ProviderName => "PostgreSQL";

    public ValueTask<IStoreContractDriver> CreateDriverAsync(CancellationToken cancellationToken) =>
        CreateDriverAsync(new ContractOptions(), cancellationToken);

    public ValueTask<IStoreContractDriver> CreateDriverAsync(
        ContractOptions options,
        CancellationToken cancellationToken)
    {
        var connectionString = Environment.GetEnvironmentVariable("OPERATIONGUARD_POSTGRESQL_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("Set OPERATIONGUARD_POSTGRESQL_CONNECTION_STRING to run PostgreSQL contracts.");
        }

        NpgsqlConnection CreateConnection() => new(connectionString);
        Func<DbConnection> connectionFactory = CreateConnection;
        var store = CreateStore(connectionFactory, options);
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
            "SELECT COUNT(*) FROM operation_guard_business_mutations WHERE business_key = @businessKey",
            "UPDATE operation_guard_operations SET scope = @scope, operation_name = @operationName, idempotency_key = @idempotencyKey",
            (action, commit, token) => ExecuteEfCoreTransactionAsync(
                connectionString,
                action,
                commit,
                token)));
    }

    private static PostgreSqlOperationStore CreateStore(
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
        var configured = typeof(PostgreSqlOperationStore).GetConstructor(
            [typeof(Func<DbConnection>), typeof(OperationGuardOptions)]);
        return configured is null
            ? new PostgreSqlOperationStore(connectionFactory)
            : (PostgreSqlOperationStore)configured.Invoke([connectionFactory, coreOptions]);
    }

    private static async ValueTask ExecuteEfCoreTransactionAsync(
        string connectionString,
        Func<System.Data.Common.DbConnection, System.Data.Common.DbTransaction, CancellationToken, ValueTask> action,
        bool commit,
        CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<ContractDbContext>()
            .UseNpgsql(connectionString)
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
