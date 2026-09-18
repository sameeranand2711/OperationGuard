using OperationGuard.Testing.Contracts.Drivers;
using OperationGuard.Testing.Contracts.Models;
using Xunit;

namespace OperationGuard.Testing.Contracts.Suites;

public abstract class ReplayPersistenceContract<TFactory>
    where TFactory : IStoreContractDriverFactory, new()
{
    private static readonly DateTimeOffset Now = new(2035, 4, 5, 6, 7, 8, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(15);
    private static readonly ContractIdentity Identity = new("tenant-replay", "Payments.Create", "key");
    private static readonly ContractFingerprint Fingerprint = ContractFingerprint.FromMarker("replay-boundary");

    [ProviderFact]
    public async Task Direct_completion_enforces_configured_replay_body_limit()
    {
        await using var driver = await CreateResetDriverAsync(new ContractOptions(ReplayBodyLimitBytes: 32));
        var begin = await BeginAsync(driver);

        await AssertSafelyRejectedOrSanitizedAsync(
            driver,
            () => driver.CompleteAsync(
                Identity,
                begin.OwnerToken!,
                Response(new byte[33]),
                true,
                "digest",
                Now.AddHours(24),
                CancellationToken.None),
            maximumBodyLength: 32);
    }

    [ProviderFact]
    public async Task Direct_completion_enforces_hard_replay_body_ceiling()
    {
        const int hardMaximum = 1024 * 1024;
        await using var driver = await CreateResetDriverAsync(new ContractOptions(ReplayBodyLimitBytes: hardMaximum));
        var begin = await BeginAsync(driver);

        await AssertSafelyRejectedOrSanitizedAsync(
            driver,
            () => driver.CompleteAsync(
                Identity,
                begin.OwnerToken!,
                Response(new byte[hardMaximum + 1]),
                true,
                "digest",
                Now.AddHours(24),
                CancellationToken.None),
            hardMaximum);
    }

    [ProviderFact]
    public async Task Direct_completion_rejects_or_filters_sensitive_and_hop_by_hop_headers()
    {
        await using var driver = await CreateResetDriverAsync();
        var begin = await BeginAsync(driver);

        await AssertSafelyRejectedOrSanitizedAsync(
            driver,
            () => driver.CompleteAsync(
                Identity,
                begin.OwnerToken!,
                UnsafeResponse(new byte[8]),
                true,
                "digest",
                Now.AddHours(24),
                CancellationToken.None),
            maximumBodyLength: 64 * 1024);
    }

    [ProviderFact]
    public async Task Transactional_session_completion_enforces_replay_safety()
    {
        await using var driver = await CreateResetDriverAsync(new ContractOptions(ReplayBodyLimitBytes: 32));
        var exception = await Record.ExceptionAsync(async () => await driver.ExecuteTransactionAsync(
            async (session, cancellationToken) =>
            {
                var begin = await session.TryBeginAsync(Identity, Fingerprint, Now, Lease, cancellationToken);
                await session.CompleteAsync(
                    Identity,
                    begin.OwnerToken!,
                    UnsafeResponse(new byte[33]),
                    true,
                    "digest",
                    Now.AddHours(24),
                    cancellationToken);
            },
            commit: true,
            CancellationToken.None));

        AssertValidationOrSuccess(exception);
        await AssertStoredReplayIsSafeAsync(driver, 32);
    }

    [ProviderFact]
    public async Task Resolve_indeterminate_enforces_replay_safety()
    {
        await using var driver = await CreateResetDriverAsync(new ContractOptions(ReplayBodyLimitBytes: 32));
        var begin = await BeginAsync(driver);
        await driver.MarkIndeterminateAsync(Identity, begin.OwnerToken!, CancellationToken.None);
        var indeterminate = await driver.ReadOutcomeAsync(Identity, CancellationToken.None);

        var exception = await Record.ExceptionAsync(async () => await driver.ResolveIndeterminateAsync(
            Identity,
            indeterminate!.RecoveryVersion,
            UnsafeResponse(new byte[33]),
            Now.AddHours(24),
            CancellationToken.None));

        AssertValidationOrSuccess(exception);
        await AssertStoredReplayIsSafeAsync(driver, 32);
    }

    private static async Task AssertSafelyRejectedOrSanitizedAsync(
        IStoreContractDriver driver,
        Func<ValueTask<ContractConditionalWriteKind>> action,
        int maximumBodyLength)
    {
        var exception = await Record.ExceptionAsync(async () =>
            Assert.Equal(ContractConditionalWriteKind.Applied, await action()));
        AssertValidationOrSuccess(exception);
        await AssertStoredReplayIsSafeAsync(driver, maximumBodyLength);
    }

    private static void AssertValidationOrSuccess(Exception? exception)
    {
        if (exception is not null)
        {
            Assert.IsAssignableFrom<ArgumentException>(exception);
        }
    }

    private static async Task AssertStoredReplayIsSafeAsync(
        IStoreContractDriver driver,
        int maximumBodyLength)
    {
        var stored = await driver.ReadOutcomeAsync(Identity, CancellationToken.None);
        if (stored?.Response is null)
        {
            return;
        }

        Assert.True(stored.Response.Body is null || stored.Response.Body.Length <= maximumBodyLength);
        Assert.All(stored.Response.Headers.Keys, AssertSafeHeader);
    }

    private static void AssertSafeHeader(string header)
    {
        Assert.DoesNotContain(
            header,
            new[] { "Authorization", "Connection", "Cookie", "Set-Cookie", "Transfer-Encoding" },
            StringComparer.OrdinalIgnoreCase);
    }

    private static ContractReplayResponse Response(byte[] body) => new(
        200,
        new Dictionary<string, string[]> { ["Content-Type"] = ["application/json"] },
        body);

    private static ContractReplayResponse UnsafeResponse(byte[] body) => new(
        200,
        new Dictionary<string, string[]>
        {
            ["Content-Type"] = ["application/json"],
            ["Set-Cookie"] = ["session=secret"],
            ["Authorization"] = ["Bearer secret"],
            ["Connection"] = ["keep-alive"],
            ["Transfer-Encoding"] = ["chunked"],
        },
        body);

    private static ValueTask<ContractBeginResult> BeginAsync(IStoreContractDriver driver) =>
        driver.TryBeginAsync(Identity, Fingerprint, Now, Lease, CancellationToken.None);

    private static async ValueTask<IStoreContractDriver> CreateResetDriverAsync(ContractOptions? options = null)
    {
        var factory = new TFactory();
        var driver = options is null
            ? await factory.CreateDriverAsync(CancellationToken.None)
            : await factory.CreateDriverAsync(options, CancellationToken.None);
        await driver.ResetAsync(CancellationToken.None);
        return driver;
    }
}
