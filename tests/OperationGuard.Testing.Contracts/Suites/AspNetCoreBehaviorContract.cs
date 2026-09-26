using System.Text;
using OperationGuard.Testing.Contracts.Drivers;
using OperationGuard.Testing.Contracts.Models;
using Xunit;

namespace OperationGuard.Testing.Contracts.Suites;

public abstract class AspNetCoreBehaviorContract<TFactory>
    where TFactory : IHttpContractDriverFactory, new()
{
    [Fact]
    public async Task Protected_endpoint_requires_idempotency_key()
    {
        await using var driver = await CreateResetDriverAsync();

        var response = await driver.SendAsync(
            Request(key: null), Handler("executed"), CancellationToken.None);

        Assert.Equal(400, response.StatusCode);
        Assert.Equal(0, driver.HandlerInvocationCount);
        AssertProblemDetails(response);
    }

    [Fact]
    public async Task Active_duplicate_returns_409_without_executing_handler()
    {
        await using var driver = await CreateResetDriverAsync();
        var request = Request();
        await driver.SeedInProgressAsync(request, CancellationToken.None);

        var response = await driver.SendAsync(request, Handler("must-not-run"), CancellationToken.None);

        Assert.Equal(409, response.StatusCode);
        Assert.Equal(0, driver.HandlerInvocationCount);
        AssertProblemDetails(response);
    }

    [Fact]
    public async Task Same_identity_with_different_raw_body_returns_422_without_reexecution()
    {
        await using var driver = await CreateResetDriverAsync();
        var first = Request(body: "{\"amount\":100}");
        var changed = Request(body: "{\"amount\":200}");
        await driver.SendAsync(first, Handler("first"), CancellationToken.None);

        var response = await driver.SendAsync(changed, Handler("must-not-run"), CancellationToken.None);

        Assert.Equal(422, response.StatusCode);
        Assert.Equal(1, driver.HandlerInvocationCount);
        AssertProblemDetails(response);
    }

    [Fact]
    public async Task Completed_duplicate_replays_without_reexecuting_handler()
    {
        await using var driver = await CreateResetDriverAsync();
        var request = Request();
        var original = Handler("created", statusCode: 201);

        var first = await driver.SendAsync(request, original, CancellationToken.None);
        var replay = await driver.SendAsync(request, Handler("must-not-run"), CancellationToken.None);

        Assert.Equal(201, first.StatusCode);
        Assert.Equal(first.StatusCode, replay.StatusCode);
        Assert.Equal(first.Body, replay.Body);
        Assert.Equal(1, driver.HandlerInvocationCount);
    }

    [Fact]
    public async Task Replay_persists_only_safe_selected_headers()
    {
        await using var driver = await CreateResetDriverAsync();
        var request = Request();
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = ["application/json"],
            ["ETag"] = ["\"version-1\""],
            ["Location"] = ["/payments/123"],
            ["Set-Cookie"] = ["session=secret"],
            ["Authorization"] = ["Bearer secret"],
            ["Connection"] = ["keep-alive"],
            ["X-Unconfigured-Secret"] = ["secret"],
        };
        await driver.SendAsync(request, Handler("created", headers: headers), CancellationToken.None);

        var replay = await driver.SendAsync(request, Handler("must-not-run"), CancellationToken.None);

        AssertHeader(replay, "Content-Type", "application/json");
        AssertHeader(replay, "ETag", "\"version-1\"");
        AssertHeader(replay, "Location", "/payments/123");
        Assert.DoesNotContain(replay.Headers.Keys, key => key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(replay.Headers.Keys, key => key.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(replay.Headers.Keys, key => key.Equals("Connection", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(replay.Headers.Keys, key => key.Equals("X-Unconfigured-Secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Oversized_response_is_completed_without_body_and_never_reexecutes()
    {
        await using var driver = await CreateResetDriverAsync();
        var request = Request();
        var oversized = new string('x', 64 * 1024 + 1);
        await driver.SendAsync(request, Handler(oversized), CancellationToken.None);

        var duplicate = await driver.SendAsync(request, Handler("must-not-run"), CancellationToken.None);
        var stored = await driver.ReadStoredOperationAsync(Identity(), CancellationToken.None);

        Assert.Equal(409, duplicate.StatusCode);
        AssertProblemDetails(duplicate);
        Assert.Equal(1, driver.HandlerInvocationCount);
        Assert.Equal(ContractOperationState.Completed, stored?.State);
        Assert.False(stored?.ReplayBodyAvailable);
        Assert.Null(stored?.Response?.Body);
        Assert.False(string.IsNullOrWhiteSpace(stored?.ResponseDigest));
    }

    [Fact]
    public async Task Ambiguous_handler_exception_becomes_indeterminate_not_retryable()
    {
        await using var driver = await CreateResetDriverAsync();
        var request = Request();

        _ = await Record.ExceptionAsync(async () =>
            await driver.SendAsync(
                request,
                Handler("", throwAfterPossibleSideEffect: true),
                CancellationToken.None));
        var afterFailure = await driver.ReadStoredOperationAsync(Identity(), CancellationToken.None);
        var retry = await driver.SendAsync(request, Handler("must-not-run"), CancellationToken.None);

        Assert.Equal(ContractOperationState.Indeterminate, afterFailure?.State);
        Assert.Equal(409, retry.StatusCode);
        Assert.Equal(1, driver.HandlerInvocationCount);
    }

    [Fact]
    public async Task Store_unavailability_fails_closed_without_invoking_handler()
    {
        await using var driver = await CreateResetDriverAsync();
        await driver.SetStoreAvailabilityAsync(false, CancellationToken.None);

        var response = await driver.SendAsync(Request(), Handler("must-not-run"), CancellationToken.None);

        Assert.InRange(response.StatusCode, 500, 599);
        Assert.Equal(0, driver.HandlerInvocationCount);
    }

    [Fact]
    public async Task Default_body_limit_rejects_more_than_one_mebibyte_before_handler()
    {
        await using var driver = await CreateResetDriverAsync();
        var request = Request(body: new string('x', 1024 * 1024 + 1));

        var response = await driver.SendAsync(request, Handler("must-not-run"), CancellationToken.None);

        Assert.InRange(response.StatusCode, 400, 499);
        Assert.Equal(0, driver.HandlerInvocationCount);
    }

    [Fact]
    public async Task Conservative_raw_json_order_causes_safe_conflict()
    {
        await using var driver = await CreateResetDriverAsync();
        await driver.SendAsync(
            Request(body: "{\"amount\":100,\"currency\":\"USD\"}"),
            Handler("created"),
            CancellationToken.None);

        var response = await driver.SendAsync(
            Request(body: "{\"currency\":\"USD\",\"amount\":100}"),
            Handler("must-not-run"),
            CancellationToken.None);

        Assert.Equal(422, response.StatusCode);
        Assert.Equal(1, driver.HandlerInvocationCount);
    }

    [Fact]
    public async Task Reversed_repeated_query_values_return_422_without_reexecution()
    {
        await using var driver = await CreateResetDriverAsync();
        await driver.SendAsync(
            Request(query: "?item=a&item=b"),
            Handler("created"),
            CancellationToken.None);

        var response = await driver.SendAsync(
            Request(query: "?item=b&item=a"),
            Handler("must-not-run"),
            CancellationToken.None);

        Assert.Equal(422, response.StatusCode);
        Assert.Equal(1, driver.HandlerInvocationCount);
        AssertProblemDetails(response);
    }

    [Fact]
    public async Task Custom_fingerprint_can_define_semantic_equivalence_explicitly()
    {
        await using var driver = await CreateResetDriverAsync();
        await driver.SendAsync(
            Request(body: "{\"amount\":100,\"currency\":\"USD\"}", useCustomFingerprint: true),
            Handler("created"),
            CancellationToken.None);

        var replay = await driver.SendAsync(
            Request(body: "{\"currency\":\"USD\",\"amount\":100}", useCustomFingerprint: true),
            Handler("must-not-run"),
            CancellationToken.None);

        Assert.Equal(200, replay.StatusCode);
        Assert.Equal(1, driver.HandlerInvocationCount);
    }

    private static ContractIdentity Identity() => new("tenant-a", "Payments.Create", "key-1");

    private static ContractHttpRequest Request(
        string? key = "key-1",
        string body = "{\"amount\":100}",
        bool useCustomFingerprint = false,
        string query = "?currency=USD") => new(
        "tenant-a",
        "Payments.Create",
        "POST",
        query,
        key,
        "application/json",
        Encoding.UTF8.GetBytes(body),
        UseCustomFingerprint: useCustomFingerprint);

    private static ContractHandlerResponse Handler(
        string body,
        int statusCode = 200,
        IReadOnlyDictionary<string, string[]>? headers = null,
        bool throwAfterPossibleSideEffect = false) => new(
        statusCode,
        headers ?? new Dictionary<string, string[]> { ["Content-Type"] = ["application/json"] },
        Encoding.UTF8.GetBytes(body),
        throwAfterPossibleSideEffect);

    private static void AssertProblemDetails(ContractHttpResponse response)
    {
        Assert.False(string.IsNullOrWhiteSpace(response.ProblemType));
        Assert.NotEmpty(response.Body);
    }

    private static void AssertHeader(ContractHttpResponse response, string name, string expected)
    {
        var pair = Assert.Single(response.Headers, pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(expected, pair.Value);
    }

    private static async ValueTask<IHttpContractDriver> CreateResetDriverAsync()
    {
        var driver = await new TFactory().CreateDriverAsync(CancellationToken.None);
        await driver.ResetAsync(CancellationToken.None);
        return driver;
    }
}
