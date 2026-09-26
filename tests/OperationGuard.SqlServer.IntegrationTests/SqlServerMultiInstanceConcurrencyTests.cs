using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using OperationGuard.Core.Models;
using OperationGuard.SqlServer.Stores;
using OperationGuard.Testing.Contracts.Suites;
using Xunit;
using Xunit.Sdk;

namespace OperationGuard.SqlServer.IntegrationTests;

public sealed class SqlServerMultiInstanceConcurrencyTests
{
    private static readonly DateTimeOffset Now = new(2035, 2, 3, 4, 5, 6, TimeSpan.Zero);
    private static readonly OperationFingerprint Fingerprint = OperationFingerprint.Sha256(
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("multi-instance"))).ToLowerInvariant());

    [ProviderFact]
    public async Task Independent_store_instances_coordinate_one_hundred_same_key_reservations()
    {
        var connectionString = ConnectionString();
        SqlConnection CreateConnection() => new(connectionString);
        var stores = Enumerable.Range(0, 10)
            .Select(_ => new SqlServerOperationStore(CreateConnection))
            .ToArray();
        await ResetAsync(stores[0]);
        var identity = new OperationIdentity("tenant-multi", "Payments.Create", "same-key");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, 100).Select(async index =>
        {
            await release.Task;
            return await stores[index % stores.Length].TryBeginAsync(
                identity,
                Fingerprint,
                Now,
                TimeSpan.FromMinutes(15),
                CancellationToken.None);
        }).ToArray();

        release.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Single(results, result => result.Kind == OperationBeginKind.Acquired);
        Assert.Equal(99, results.Count(result => result.Kind == OperationBeginKind.AlreadyInProgress));
    }

    [ProviderFact]
    public async Task Independent_store_instances_coordinate_distinct_keys_and_a_hot_key()
    {
        var connectionString = ConnectionString();
        SqlConnection CreateConnection() => new(connectionString);
        var stores = Enumerable.Range(0, 10)
            .Select(_ => new SqlServerOperationStore(CreateConnection))
            .ToArray();
        await ResetAsync(stores[0]);
        var hotIdentity = new OperationIdentity("tenant-multi", "Payments.Create", "hot-key");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = Enumerable.Range(0, 100)
            .Select(index => (index, identity: new OperationIdentity("tenant-multi", "Payments.Create", $"distinct-{index}")))
            .Concat(Enumerable.Range(100, 100).Select(index => (index, identity: hotIdentity)))
            .Select(async item =>
            {
                await release.Task;
                return (item.identity, result: await stores[item.index % stores.Length].TryBeginAsync(
                    item.identity,
                    Fingerprint,
                    Now,
                    TimeSpan.FromMinutes(15),
                    CancellationToken.None));
            })
            .ToArray();

        release.SetResult();
        var results = await Task.WhenAll(work);

        Assert.All(results.Where(item => item.identity != hotIdentity), item => Assert.Equal(OperationBeginKind.Acquired, item.result.Kind));
        Assert.Single(results.Where(item => item.identity == hotIdentity), item => item.result.Kind == OperationBeginKind.Acquired);
        Assert.Equal(99, results.Count(item => item.identity == hotIdentity && item.result.Kind == OperationBeginKind.AlreadyInProgress));
    }

    private static string ConnectionString() =>
        Environment.GetEnvironmentVariable("OPERATIONGUARD_SQLSERVER_CONNECTION_STRING") is { Length: > 0 } value
            ? value
            : throw SkipException.ForSkip("Set OPERATIONGUARD_SQLSERVER_CONNECTION_STRING to run SQL Server concurrency tests.");

    private static async Task ResetAsync(SqlServerOperationStore store)
    {
        await store.EnsureCreatedAsync(CancellationToken.None);
        await using var connection = new SqlConnection(ConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dbo.OperationGuardOperations";
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
