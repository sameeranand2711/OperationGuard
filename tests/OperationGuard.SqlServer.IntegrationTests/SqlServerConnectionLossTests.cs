using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using OperationGuard.Core.Models;
using OperationGuard.SqlServer.Stores;
using OperationGuard.Testing.Contracts.Suites;
using Xunit;
using Xunit.Sdk;

namespace OperationGuard.SqlServer.IntegrationTests;

public sealed class SqlServerConnectionLossTests
{
    [ProviderFact]
    public async Task Connection_loss_after_reservation_does_not_make_operation_retryable()
    {
        var containerName = Environment.GetEnvironmentVariable("OPERATIONGUARD_SQLSERVER_CONTAINER_NAME");
        if (string.IsNullOrWhiteSpace(containerName))
        {
            throw SkipException.ForSkip(
                "Set OPERATIONGUARD_SQLSERVER_CONTAINER_NAME to opt into the Docker connection-loss fault test.");
        }

        var builder = new SqlConnectionStringBuilder(ConnectionString())
        {
            ConnectTimeout = 2,
            Pooling = false,
        };
        SqlConnection CreateConnection() => new(builder.ConnectionString);
        var store = new SqlServerOperationStore(CreateConnection);
        await EnsureReadyAsync(store);
        await ResetAsync(CreateConnection);
        var identity = new OperationIdentity("tenant-fault", "Payments.Create", "connection-loss");
        var fingerprint = OperationFingerprint.Sha256(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("connection-loss"))).ToLowerInvariant());
        var begin = await store.TryBeginAsync(
            identity,
            fingerprint,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(15));
        Assert.Equal(OperationBeginKind.Acquired, begin.Kind);

        await RunDockerAsync("pause", containerName);
        try
        {
            await Assert.ThrowsAsync<SqlException>(async () => await store.CompleteAsync(
                identity,
                begin.OwnerToken!,
                response: null,
                replayBodyAvailable: false,
                responseDigest: null,
                DateTimeOffset.UtcNow.AddHours(24)));
        }
        finally
        {
            await RunDockerAsync("unpause", containerName);
        }

        var stored = await ReadAfterRecoveryAsync(store, identity);
        var retry = await store.TryBeginAsync(
            identity,
            fingerprint,
            DateTimeOffset.UtcNow.AddHours(1),
            TimeSpan.FromMinutes(15));

        Assert.Equal(OperationState.InProgress, stored?.State);
        Assert.Equal(OperationBeginKind.AlreadyInProgress, retry.Kind);
    }

    private static string ConnectionString() =>
        Environment.GetEnvironmentVariable("OPERATIONGUARD_SQLSERVER_CONNECTION_STRING") is { Length: > 0 } value
            ? value
            : throw SkipException.ForSkip("Set OPERATIONGUARD_SQLSERVER_CONNECTION_STRING to run SQL Server fault tests.");

    private static async Task ResetAsync(Func<SqlConnection> connectionFactory)
    {
        await using var connection = connectionFactory();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dbo.OperationGuardOperations";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<StoredOperation?> ReadAfterRecoveryAsync(
        SqlServerOperationStore store,
        OperationIdentity identity)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                return await store.ReadOutcomeAsync(identity);
            }
            catch (SqlException exception)
            {
                last = exception;
                await Task.Delay(250);
            }
        }

        throw new InvalidOperationException("SQL Server did not recover after the injected connection loss.", last);
    }

    private static async Task EnsureReadyAsync(SqlServerOperationStore store)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                await store.EnsureCreatedAsync();
                return;
            }
            catch (SqlException exception)
            {
                last = exception;
                await Task.Delay(250);
            }
        }

        throw new InvalidOperationException("SQL Server was not ready for fault injection.", last);
    }

    private static async Task RunDockerAsync(string verb, string containerName)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "docker",
            ArgumentList = { verb, containerName },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Could not start Docker CLI.");
        var standardError = process.StandardError.ReadToEndAsync();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker {verb} failed: {await standardError} {await standardOutput}");
        }
    }
}
