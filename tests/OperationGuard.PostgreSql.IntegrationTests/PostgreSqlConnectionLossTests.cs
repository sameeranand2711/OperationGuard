using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using OperationGuard.Core.Models;
using OperationGuard.PostgreSql.Stores;
using OperationGuard.Testing.Contracts.Suites;
using Xunit;
using Xunit.Sdk;

namespace OperationGuard.PostgreSql.IntegrationTests;

public sealed class PostgreSqlConnectionLossTests
{
    [ProviderFact]
    public async Task Connection_loss_after_reservation_does_not_make_operation_retryable()
    {
        var containerName = Environment.GetEnvironmentVariable("OPERATIONGUARD_POSTGRESQL_CONTAINER_NAME");
        if (string.IsNullOrWhiteSpace(containerName))
        {
            throw SkipException.ForSkip(
                "Set OPERATIONGUARD_POSTGRESQL_CONTAINER_NAME to opt into the Docker connection-loss fault test.");
        }

        var builder = new NpgsqlConnectionStringBuilder(ConnectionString())
        {
            Timeout = 2,
            CommandTimeout = 2,
            Pooling = false,
        };
        NpgsqlConnection CreateConnection() => new(builder.ConnectionString);
        var store = new PostgreSqlOperationStore(CreateConnection);
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
            await Assert.ThrowsAnyAsync<Exception>(async () => await store.CompleteAsync(
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
        Environment.GetEnvironmentVariable("OPERATIONGUARD_POSTGRESQL_CONNECTION_STRING") is { Length: > 0 } value
            ? value
            : throw SkipException.ForSkip("Set OPERATIONGUARD_POSTGRESQL_CONNECTION_STRING to run PostgreSQL fault tests.");

    private static async Task ResetAsync(Func<NpgsqlConnection> connectionFactory)
    {
        await using var connection = connectionFactory();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM operation_guard_operations";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<StoredOperation?> ReadAfterRecoveryAsync(
        PostgreSqlOperationStore store,
        OperationIdentity identity)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                return await store.ReadOutcomeAsync(identity);
            }
            catch (Exception exception) when (exception is NpgsqlException or TimeoutException or IOException)
            {
                last = exception;
                await Task.Delay(250);
            }
        }

        throw new InvalidOperationException("PostgreSQL did not recover after the injected connection loss.", last);
    }

    private static async Task EnsureReadyAsync(PostgreSqlOperationStore store)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                await store.EnsureCreatedAsync();
                return;
            }
            catch (Exception exception) when (exception is NpgsqlException or TimeoutException or IOException)
            {
                last = exception;
                await Task.Delay(250);
            }
        }

        throw new InvalidOperationException("PostgreSQL was not ready for fault injection.", last);
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
