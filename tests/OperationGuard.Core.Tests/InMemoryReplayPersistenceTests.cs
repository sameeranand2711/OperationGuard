using System.Reflection;
using System.Text;
using System.Text.Json;
using OperationGuard.Core;
using OperationGuard.Core.Models;
using OperationGuard.Core.Testing;
using OperationGuard.Testing.Contracts.Drivers;
using OperationGuard.Testing.Contracts.Models;
using Xunit;

namespace OperationGuard.Core.Tests;

public sealed class InMemoryReplayPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2035, 8, 9, 10, 11, 12, TimeSpan.Zero);
    private static readonly OperationFingerprint Fingerprint = OperationFingerprint.Sha256(new string('a', 64));

    [Fact]
    public async Task In_memory_store_accepts_exact_utf8_and_serialized_header_boundaries()
    {
        var response = ExactBoundaryResponse();
        var options = new ContractOptions(
            MaximumReplayHeaderCount: response.Headers.Count,
            MaximumReplayHeaderValueBytes: response.Headers.Values
                .SelectMany(static values => values)
                .Max(static value => Encoding.UTF8.GetByteCount(value)),
            MaximumReplayHeadersTotalBytes: JsonSerializer.SerializeToUtf8Bytes(response.Headers).Length);
        var store = CreateStore(options);
        var identity = Identity("exact");
        var begin = await BeginAsync(store, identity);

        var result = await store.CompleteAsync(
            identity,
            begin.OwnerToken!,
            response,
            true,
            "digest",
            Now.AddHours(24));

        Assert.Equal(ConditionalWriteKind.Applied, result);
        var stored = await store.ReadOutcomeAsync(identity);
        Assert.NotNull(stored?.Response);
        Assert.Equal(response.StatusCode, stored.Response.StatusCode);
        Assert.Equal(response.Body, stored.Response.Body);
        Assert.Equal(response.Headers.Keys, stored.Response.Headers.Keys);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(599)]
    public async Task In_memory_store_accepts_safe_final_status_code_boundaries(int statusCode)
    {
        var store = CreateStore(new ContractOptions());
        var identity = Identity($"status-{statusCode}");
        var begin = await BeginAsync(store, identity);

        var result = await store.CompleteAsync(
            identity,
            begin.OwnerToken!,
            Response(statusCode, new Dictionary<string, string[]>()),
            true,
            "digest",
            Now.AddHours(24));

        Assert.Equal(ConditionalWriteKind.Applied, result);
    }

    [Fact]
    public async Task In_memory_store_accepts_horizontal_tab_in_replay_header_values()
    {
        var store = CreateStore(new ContractOptions());
        var identity = Identity("horizontal-tab");
        var begin = await BeginAsync(store, identity);

        var result = await store.CompleteAsync(
            identity,
            begin.OwnerToken!,
            Response(200, new Dictionary<string, string[]> { ["X-Value"] = ["one\ttwo"] }),
            true,
            "digest",
            Now.AddHours(24));
        var stored = await store.ReadOutcomeAsync(identity);

        Assert.Equal(ConditionalWriteKind.Applied, result);
        Assert.Equal("one\ttwo", Assert.Single(stored?.Response?.Headers["X-Value"]!));
    }

    [Fact]
    public async Task In_memory_direct_completion_rejects_before_changing_state()
    {
        foreach (var invalidCase in InvalidCases())
        {
            var store = CreateStore(invalidCase.Options);
            var identity = Identity($"direct-{invalidCase.Name}");
            var begin = await BeginAsync(store, identity);

            await Assert.ThrowsAnyAsync<ArgumentException>(async () => await store.CompleteAsync(
                identity,
                begin.OwnerToken!,
                invalidCase.Response,
                true,
                "digest",
                Now.AddHours(24)));

            var stored = await store.ReadOutcomeAsync(identity);
            Assert.Equal(OperationState.InProgress, stored?.State);
            Assert.Null(stored?.Response);
        }
    }

    [Fact]
    public async Task In_memory_indeterminate_resolution_rejects_before_changing_state()
    {
        foreach (var invalidCase in InvalidCases())
        {
            var store = CreateStore(invalidCase.Options);
            var identity = Identity($"resolve-{invalidCase.Name}");
            var begin = await BeginAsync(store, identity);
            await store.MarkIndeterminateAsync(identity, begin.OwnerToken!);
            var indeterminate = await store.ReadOutcomeAsync(identity);
            Assert.NotNull(indeterminate);

            await Assert.ThrowsAnyAsync<ArgumentException>(async () => await store.ResolveIndeterminateAsync(
                identity,
                indeterminate.RecoveryVersion,
                invalidCase.Response,
                Now.AddHours(24)));

            var stored = await store.ReadOutcomeAsync(identity);
            Assert.Equal(OperationState.Indeterminate, stored?.State);
            Assert.Equal(indeterminate.RecoveryVersion, stored?.RecoveryVersion);
            Assert.Null(stored?.Response);
        }
    }

    private static IEnumerable<InvalidCase> InvalidCases()
    {
        yield return new InvalidCase(
            "count",
            new ContractOptions(
                MaximumReplayHeaderCount: 1,
                MaximumReplayHeaderValueBytes: 128,
                MaximumReplayHeadersTotalBytes: 1024),
            Response(200, new Dictionary<string, string[]>
            {
                ["X-One"] = ["one"],
                ["X-Two"] = ["two"],
            }));
        yield return new InvalidCase(
            "multibyte-value",
            new ContractOptions(
                MaximumReplayHeaderCount: 8,
                MaximumReplayHeaderValueBytes: 1024,
                MaximumReplayHeadersTotalBytes: 16 * 1024),
            Response(200, new Dictionary<string, string[]> { ["X-Value"] = [Utf8Value(1025)] }));

        var headers = new Dictionary<string, string[]> { ["X-One"] = ["12345678"] };
        var serializedLength = JsonSerializer.SerializeToUtf8Bytes(headers).Length;
        yield return new InvalidCase(
            "aggregate",
            new ContractOptions(
                MaximumReplayHeaderCount: 8,
                MaximumReplayHeaderValueBytes: 16,
                MaximumReplayHeadersTotalBytes: serializedLength - 1),
            Response(200, headers));
        yield return new InvalidCase(
            "body",
            new ContractOptions(ReplayBodyLimitBytes: 1),
            new ReplayResponse(200, new Dictionary<string, string[]>(), new byte[2]));
        yield return new InvalidCase(
            "invalid-header-name",
            new ContractOptions(),
            Response(200, new Dictionary<string, string[]> { ["Invalid Header"] = ["value"] }));
        yield return new InvalidCase(
            "forbidden-header",
            new ContractOptions(),
            Response(200, new Dictionary<string, string[]> { ["Set-Cookie"] = ["session=secret"] }));
        yield return new InvalidCase(
            "newline-value",
            new ContractOptions(),
            Response(200, new Dictionary<string, string[]> { ["Location"] = ["/ok\r\nX-Injected: secret"] }));
        foreach (var invalidControl in InvalidHeaderValueControls())
        {
            yield return new InvalidCase(
                $"control-u{(int)invalidControl:x4}",
                new ContractOptions(),
                Response(200, new Dictionary<string, string[]> { ["X-Value"] = [$"one{invalidControl}two"] }));
        }

        yield return new InvalidCase(
            "null-value-array",
            new ContractOptions(),
            Response(200, new Dictionary<string, string[]> { ["Location"] = null! }));
        yield return new InvalidCase("status-low", new ContractOptions(), Response(199, new Dictionary<string, string[]>()));
        yield return new InvalidCase("status-high", new ContractOptions(), Response(600, new Dictionary<string, string[]>()));
    }

    private static IEnumerable<char> InvalidHeaderValueControls() =>
        new[] { '\u007f' }.Concat(Enumerable.Range(0, 32)
            .Select(static value => (char)value)
            .Where(static value => value != '\t'));

    private static InMemoryOperationStore CreateStore(ContractOptions options)
    {
        var coreOptions = new OperationGuardOptions
        {
            ReplayBodyLimitBytes = options.ReplayBodyLimitBytes,
        };
        ContractOptionsBinding.TryApplyReplayHeaderLimits(coreOptions, options);
        var constructor = typeof(InMemoryOperationStore).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            [typeof(OperationGuardOptions)],
            modifiers: null)
            ?? throw new MissingMethodException(
                typeof(InMemoryOperationStore).FullName,
                ".ctor(OperationGuardOptions)");
        return (InMemoryOperationStore)constructor.Invoke([coreOptions]);
    }

    private static ReplayResponse ExactBoundaryResponse() => Response(
        200,
        new Dictionary<string, string[]>
        {
            ["X-Ascii"] = ["value"],
            ["X-Multibyte"] = [Utf8Value(1024)],
        });

    private static ReplayResponse Response(int statusCode, IReadOnlyDictionary<string, string[]> headers) =>
        new(statusCode, headers, Encoding.UTF8.GetBytes("ok"));

    private static string Utf8Value(int byteCount)
    {
        const string multibyte = "€";
        var fullCharacters = byteCount / Encoding.UTF8.GetByteCount(multibyte);
        var remainder = byteCount % Encoding.UTF8.GetByteCount(multibyte);
        return string.Concat(Enumerable.Repeat(multibyte, fullCharacters)) + new string('a', remainder);
    }

    private static OperationIdentity Identity(string key) => new("tenant", "Payments.Create", key);

    private static ValueTask<OperationBeginResult> BeginAsync(
        InMemoryOperationStore store,
        OperationIdentity identity) =>
        store.TryBeginAsync(identity, Fingerprint, Now, TimeSpan.FromMinutes(15));

    private sealed record InvalidCase(string Name, ContractOptions Options, ReplayResponse Response);
}
