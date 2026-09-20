using System.Text;
using System.Text.Json;
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
    public async Task Direct_completion_accepts_exact_header_count_value_and_serialized_boundaries()
    {
        var response = ExactBoundaryResponse();
        var options = ExactBoundaryOptions(response);
        await using var driver = await CreateResetDriverAsync(options);
        var begin = await BeginAsync(driver, Identity);

        var result = await driver.CompleteAsync(
            Identity,
            begin.OwnerToken!,
            response,
            true,
            "digest",
            Now.AddHours(24),
            TestContext.Current.CancellationToken);
        var stored = await driver.ReadOutcomeAsync(Identity, TestContext.Current.CancellationToken);

        Assert.Equal(ContractConditionalWriteKind.Applied, result);
        Assert.NotNull(stored?.Response);
        Assert.Equal(response.StatusCode, stored.Response.StatusCode);
        Assert.Equal(response.Body, stored.Response.Body);
        Assert.Equal(
            response.Headers.OrderBy(static header => header.Key, StringComparer.Ordinal),
            stored.Response.Headers.OrderBy(static header => header.Key, StringComparer.Ordinal),
            HeaderComparer.Instance);
    }

    [ProviderFact]
    public async Task Direct_completion_atomically_rejects_each_replay_persistence_violation()
    {
        foreach (var invalidCase in InvalidReplayCases())
        {
            await using var driver = await CreateResetDriverAsync(invalidCase.Options);
            var identity = Identity with { IdempotencyKey = $"direct-{invalidCase.Name}" };
            var begin = await BeginAsync(driver, identity);

            await Assert.ThrowsAnyAsync<ArgumentException>(async () => await driver.CompleteAsync(
                identity,
                begin.OwnerToken!,
                invalidCase.Response,
                true,
                "digest",
                Now.AddHours(24),
                TestContext.Current.CancellationToken));

            var stored = await driver.ReadOutcomeAsync(identity, TestContext.Current.CancellationToken);
            Assert.Equal(ContractOperationState.InProgress, stored?.State);
            Assert.Null(stored?.Response);
        }
    }

    [ProviderFact]
    public async Task Transactional_completion_atomically_rejects_each_replay_persistence_violation()
    {
        foreach (var invalidCase in InvalidReplayCases())
        {
            await using var driver = await CreateResetDriverAsync(invalidCase.Options);
            var identity = Identity with { IdempotencyKey = $"transactional-{invalidCase.Name}" };
            var businessKey = $"business-{invalidCase.Name}";

            await Assert.ThrowsAnyAsync<ArgumentException>(async () => await driver.ExecuteTransactionAsync(
                async (session, cancellationToken) =>
                {
                    var begin = await session.TryBeginAsync(identity, Fingerprint, Now, Lease, cancellationToken);
                    await session.AddBusinessMutationAsync(businessKey, cancellationToken);
                    await session.CompleteAsync(
                        identity,
                        begin.OwnerToken!,
                        invalidCase.Response,
                        true,
                        "digest",
                        Now.AddHours(24),
                        cancellationToken);
                },
                commit: true,
                TestContext.Current.CancellationToken));

            Assert.Null(await driver.ReadOutcomeAsync(identity, TestContext.Current.CancellationToken));
            Assert.Equal(0, await driver.CountBusinessMutationsAsync(
                businessKey,
                TestContext.Current.CancellationToken));
        }
    }

    [ProviderFact]
    public async Task Resolve_indeterminate_atomically_rejects_each_replay_persistence_violation()
    {
        foreach (var invalidCase in InvalidReplayCases())
        {
            await using var driver = await CreateResetDriverAsync(invalidCase.Options);
            var identity = Identity with { IdempotencyKey = $"resolve-{invalidCase.Name}" };
            var begin = await BeginAsync(driver, identity);
            await driver.MarkIndeterminateAsync(identity, begin.OwnerToken!, TestContext.Current.CancellationToken);
            var indeterminate = await driver.ReadOutcomeAsync(identity, TestContext.Current.CancellationToken);
            Assert.NotNull(indeterminate);

            await Assert.ThrowsAnyAsync<ArgumentException>(async () => await driver.ResolveIndeterminateAsync(
                identity,
                indeterminate.RecoveryVersion,
                invalidCase.Response,
                Now.AddHours(24),
                TestContext.Current.CancellationToken));

            var stored = await driver.ReadOutcomeAsync(identity, TestContext.Current.CancellationToken);
            Assert.Equal(ContractOperationState.Indeterminate, stored?.State);
            Assert.Equal(indeterminate.RecoveryVersion, stored?.RecoveryVersion);
            Assert.Null(stored?.Response);
        }
    }

    [ProviderFact]
    public async Task Direct_completion_accepts_safe_final_status_code_boundaries()
    {
        await using var driver = await CreateResetDriverAsync();
        foreach (var statusCode in new[] { 200, 599 })
        {
            var identity = Identity with { IdempotencyKey = $"status-{statusCode}" };
            var begin = await BeginAsync(driver, identity);
            var result = await driver.CompleteAsync(
                identity,
                begin.OwnerToken!,
                Response(statusCode, new Dictionary<string, string[]>()),
                true,
                "digest",
                Now.AddHours(24),
                TestContext.Current.CancellationToken);

            Assert.Equal(ContractConditionalWriteKind.Applied, result);
        }
    }

    [ProviderFact]
    public async Task Direct_completion_accepts_horizontal_tab_in_replay_header_values()
    {
        await using var driver = await CreateResetDriverAsync();
        var identity = Identity with { IdempotencyKey = "horizontal-tab" };
        var begin = await BeginAsync(driver, identity);
        var response = Response(
            200,
            new Dictionary<string, string[]> { ["X-Value"] = ["one\ttwo"] });

        var result = await driver.CompleteAsync(
            identity,
            begin.OwnerToken!,
            response,
            true,
            "digest",
            Now.AddHours(24),
            TestContext.Current.CancellationToken);
        var stored = await driver.ReadOutcomeAsync(identity, TestContext.Current.CancellationToken);

        Assert.Equal(ContractConditionalWriteKind.Applied, result);
        Assert.Equal("one\ttwo", Assert.Single(stored?.Response?.Headers["X-Value"]!));
    }

    [ProviderFact]
    public async Task Direct_completion_enforces_configured_replay_body_limit()
    {
        await using var driver = await CreateResetDriverAsync(new ContractOptions(ReplayBodyLimitBytes: 32));
        var begin = await BeginAsync(driver, Identity);

        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await driver.CompleteAsync(
            Identity,
            begin.OwnerToken!,
            Response(200, new Dictionary<string, string[]>(), new byte[33]),
            true,
            "digest",
            Now.AddHours(24),
            TestContext.Current.CancellationToken));
    }

    private static IEnumerable<InvalidReplayCase> InvalidReplayCases()
    {
        yield return new InvalidReplayCase(
            "configured-body-limit",
            new ContractOptions(ReplayBodyLimitBytes: 32),
            Response(200, new Dictionary<string, string[]>(), new byte[33]));
        yield return new InvalidReplayCase(
            "hard-body-limit",
            new ContractOptions(ReplayBodyLimitBytes: 1024 * 1024),
            Response(200, new Dictionary<string, string[]>(), new byte[1024 * 1024 + 1]));

        yield return new InvalidReplayCase(
            "header-count",
            new ContractOptions(
                MaximumReplayHeaderCount: 3,
                MaximumReplayHeaderValueBytes: 128,
                MaximumReplayHeadersTotalBytes: 2048),
            Response(200, Enumerable.Range(0, 4).ToDictionary(
                index => $"X-Header-{index}",
                _ => new[] { "value" },
                StringComparer.OrdinalIgnoreCase)));

        yield return new InvalidReplayCase(
            "multibyte-value",
            new ContractOptions(
                MaximumReplayHeaderCount: 8,
                MaximumReplayHeaderValueBytes: 1024,
                MaximumReplayHeadersTotalBytes: 16 * 1024),
            Response(200, new Dictionary<string, string[]>
            {
                ["X-Multibyte"] = [Utf8Value(1025)],
            }));

        var aggregateHeaders = new Dictionary<string, string[]>
        {
            ["X-One"] = ["12345678"],
            ["X-Two"] = ["abcdefgh"],
        };
        var aggregateSize = JsonSerializer.SerializeToUtf8Bytes(aggregateHeaders).Length;
        yield return new InvalidReplayCase(
            "serialized-total",
            new ContractOptions(
                MaximumReplayHeaderCount: 8,
                MaximumReplayHeaderValueBytes: 16,
                MaximumReplayHeadersTotalBytes: aggregateSize - 1),
            Response(200, aggregateHeaders));

        yield return new InvalidReplayCase(
            "invalid-header-name",
            new ContractOptions(),
            Response(200, new Dictionary<string, string[]> { ["Invalid Header"] = ["value"] }));
        yield return new InvalidReplayCase(
            "forbidden-header",
            new ContractOptions(),
            Response(200, new Dictionary<string, string[]> { ["Set-Cookie"] = ["session=secret"] }));
        yield return new InvalidReplayCase(
            "newline-value",
            new ContractOptions(),
            Response(200, new Dictionary<string, string[]> { ["Location"] = ["/ok\r\nX-Injected: secret"] }));
        foreach (var invalidControl in InvalidHeaderValueControls())
        {
            yield return new InvalidReplayCase(
                $"control-u{(int)invalidControl:x4}",
                new ContractOptions(),
                Response(200, new Dictionary<string, string[]> { ["X-Value"] = [$"one{invalidControl}two"] }));
        }

        yield return new InvalidReplayCase(
            "null-value-array",
            new ContractOptions(),
            Response(200, new Dictionary<string, string[]> { ["Location"] = null! }));

        yield return new InvalidReplayCase(
            "status-199",
            new ContractOptions(),
            Response(199, new Dictionary<string, string[]>()));
        yield return new InvalidReplayCase(
            "status-600",
            new ContractOptions(),
            Response(600, new Dictionary<string, string[]>()));
    }

    private static IEnumerable<char> InvalidHeaderValueControls() =>
        new[] { '\u007f' }.Concat(Enumerable.Range(0, 32)
            .Select(static value => (char)value)
            .Where(static value => value != '\t'));

    private static ContractReplayResponse ExactBoundaryResponse() => Response(
        200,
        new Dictionary<string, string[]>
        {
            ["X-Ascii"] = ["value"],
            ["X-Multibyte"] = [Utf8Value(1024)],
            ["Content-Type"] = ["application/json"],
        });

    private static ContractOptions ExactBoundaryOptions(ContractReplayResponse response) => new(
        MaximumReplayHeaderCount: response.Headers.Count,
        MaximumReplayHeaderValueBytes: response.Headers.Values
            .SelectMany(static values => values)
            .Max(static value => Encoding.UTF8.GetByteCount(value)),
        MaximumReplayHeadersTotalBytes: JsonSerializer.SerializeToUtf8Bytes(response.Headers).Length);

    private static string Utf8Value(int byteCount)
    {
        const string multibyte = "€";
        var fullCharacters = byteCount / Encoding.UTF8.GetByteCount(multibyte);
        var remainder = byteCount % Encoding.UTF8.GetByteCount(multibyte);
        return string.Concat(Enumerable.Repeat(multibyte, fullCharacters)) + new string('a', remainder);
    }

    private static ContractReplayResponse Response(
        int statusCode,
        IReadOnlyDictionary<string, string[]> headers,
        byte[]? body = null) => new(statusCode, headers, body ?? Encoding.UTF8.GetBytes("ok"));

    private static ValueTask<ContractBeginResult> BeginAsync(
        IStoreContractDriver driver,
        ContractIdentity identity) =>
        driver.TryBeginAsync(identity, Fingerprint, Now, Lease, TestContext.Current.CancellationToken);

    private static async ValueTask<IStoreContractDriver> CreateResetDriverAsync(ContractOptions? options = null)
    {
        var factory = new TFactory();
        var driver = options is null
            ? await factory.CreateDriverAsync(TestContext.Current.CancellationToken)
            : await factory.CreateDriverAsync(options, TestContext.Current.CancellationToken);
        await driver.ResetAsync(TestContext.Current.CancellationToken);
        return driver;
    }

    private sealed record InvalidReplayCase(
        string Name,
        ContractOptions Options,
        ContractReplayResponse Response);

    private sealed class HeaderComparer : IEqualityComparer<KeyValuePair<string, string[]>>
    {
        internal static HeaderComparer Instance { get; } = new();

        public bool Equals(
            KeyValuePair<string, string[]> left,
            KeyValuePair<string, string[]> right) =>
            StringComparer.Ordinal.Equals(left.Key, right.Key)
            && left.Value.SequenceEqual(right.Value, StringComparer.Ordinal);

        public int GetHashCode(KeyValuePair<string, string[]> value) =>
            StringComparer.Ordinal.GetHashCode(value.Key);
    }
}
