using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OperationGuard.AspNetCore.Metadata;
using OperationGuard.Core;
using OperationGuard.Core.Fingerprinting;
using OperationGuard.Core.Models;
using OperationGuard.Core.Testing;
using Xunit;

namespace OperationGuard.AspNetCore.Tests;

public sealed class DefensiveReplayTests
{
    private static readonly DateTimeOffset Now = new(2035, 6, 7, 8, 9, 10, TimeSpan.Zero);

    [Fact]
    public async Task Duplicate_replay_defensively_filters_hostile_persisted_headers()
    {
        var store = new InMemoryOperationStore();
        var identity = Identity("hostile-headers");
        await SeedCompletedAsync(
            store,
            identity,
            new ReplayResponse(
                200,
                new Dictionary<string, string[]>
                {
                    ["Content-Type"] = ["application/json"],
                    ["Set-Cookie"] = ["session=secret"],
                    ["Authorization"] = ["Bearer secret"],
                    ["Connection"] = ["keep-alive"],
                    ["Transfer-Encoding"] = ["chunked"],
                    ["X-Unconfigured-Secret"] = ["secret"],
                },
                Encoding.UTF8.GetBytes("ok")));

        var response = await ReplayAsync(store, identity);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("Content-Type", response.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Set-Cookie", response.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", response.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Connection", response.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Transfer-Encoding", response.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Unconfigured-Secret", response.Headers.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Duplicate_replay_refuses_hostile_persisted_body_above_configured_limit()
    {
        var store = new InMemoryOperationStore();
        var identity = Identity("hostile-body");
        await SeedCompletedAsync(
            store,
            identity,
            new ReplayResponse(200, new Dictionary<string, string[]>(), new byte[33]));

        var response = await ReplayAsync(store, identity, replayLimit: 32);

        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        Assert.NotEqual(33, response.Body.Length);
    }

    private static async Task SeedCompletedAsync(
        InMemoryOperationStore store,
        OperationIdentity identity,
        ReplayResponse response)
    {
        var fingerprint = await new Sha256RequestFingerprintProvider().CreateAsync(
            new FingerprintInput(
                identity.OperationName,
                HttpMethods.Post,
                "?currency=USD",
                "application/json",
                Encoding.UTF8.GetBytes("request"),
                new Dictionary<string, string[]>()));
        var begin = await store.TryBeginAsync(
            identity,
            fingerprint,
            Now,
            TimeSpan.FromMinutes(15));
        Assert.Equal(
            ConditionalWriteKind.Applied,
            await store.CompleteAsync(
                identity,
                begin.OwnerToken!,
                response,
                replayBodyAvailable: true,
                responseDigest: "digest",
                Now.AddHours(24)));
    }

    private static async Task<ReplayResult> ReplayAsync(
        InMemoryOperationStore store,
        OperationIdentity identity,
        int replayLimit = 64 * 1024)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.QueryString = new QueryString("?currency=USD");
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("request"));
        context.Request.ContentLength = context.Request.Body.Length;
        context.Request.Headers["Idempotency-Key"] = identity.IdempotencyKey;
        context.Items["scope"] = identity.Scope;
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(new Endpoint(
            null,
            new EndpointMetadataCollection(new OperationGuardEndpointMetadata(identity.OperationName)),
            identity.OperationName));
        var middleware = new OperationGuardMiddleware(
            _ => throw new InvalidOperationException("A completed duplicate must never execute."),
            new OperationGuardAspNetCoreOptions
            {
                ReplayBodyLimitBytes = replayLimit,
                ScopeResolver = static (httpContext, _) =>
                    ValueTask.FromResult((string)httpContext.Items["scope"]!),
            },
            new OperationGuardOptions { ReplayBodyLimitBytes = replayLimit },
            new FixedTimeProvider(Now),
            NullLogger<OperationGuardMiddleware>.Instance);

        await middleware.InvokeAsync(context, store, new Sha256RequestFingerprintProvider());
        context.Response.Body.Position = 0;
        using var copy = new MemoryStream();
        await context.Response.Body.CopyToAsync(copy);
        return new ReplayResult(
            context.Response.StatusCode,
            context.Response.Headers.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Select(value => value!).ToArray(),
                StringComparer.OrdinalIgnoreCase),
            copy.ToArray());
    }

    private static OperationIdentity Identity(string key) =>
        new("tenant", "Payments.Create", key);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record ReplayResult(
        int StatusCode,
        IReadOnlyDictionary<string, string[]> Headers,
        byte[] Body);
}
