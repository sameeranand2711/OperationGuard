using System.Data.Common;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OperationGuard.AspNetCore.Metadata;
using OperationGuard.Core;
using OperationGuard.Core.Abstractions;
using OperationGuard.Core.Fingerprinting;
using OperationGuard.Core.Models;
using OperationGuard.Testing.Contracts.Drivers;
using OperationGuard.Testing.Contracts.Models;
using Xunit;

namespace OperationGuard.AspNetCore.Tests;

public sealed class DefensiveReplayTests
{
    private static readonly DateTimeOffset Now = new(2035, 6, 7, 8, 9, 10, TimeSpan.Zero);

    [Fact]
    public async Task Hostile_store_replay_rejects_all_oversized_or_corrupt_headers_before_adding_any_header()
    {
        foreach (var invalidCase in InvalidHeaderCases())
        {
            var response = await ReplayAsync(invalidCase.Response, invalidCase.Options);

            Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
            Assert.DoesNotContain("ETag", response.Headers.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("Location", response.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(199)]
    [InlineData(600)]
    public async Task Hostile_store_replay_rejects_non_final_or_out_of_range_status_before_adding_headers(
        int statusCode)
    {
        var response = await ReplayAsync(
            Response(statusCode, new Dictionary<string, string[]> { ["ETag"] = ["safe-first"] }),
            new ContractOptions());

        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        Assert.DoesNotContain("ETag", response.Headers.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Hostile_store_replay_accepts_exact_utf8_and_serialized_header_boundaries()
    {
        var persisted = Response(
            200,
            new Dictionary<string, string[]>
            {
                ["ETag"] = [Utf8Value(1024)],
                ["Location"] = ["/payments/123"],
            });
        var options = new ContractOptions(
            MaximumReplayHeaderCount: persisted.Headers.Count,
            MaximumReplayHeaderValueBytes: 1024,
            MaximumReplayHeadersTotalBytes: JsonSerializer.SerializeToUtf8Bytes(persisted.Headers).Length);

        var response = await ReplayAsync(persisted, options);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal(Utf8Value(1024), Assert.Single(response.Headers["ETag"]));
        Assert.Equal("ok", Encoding.UTF8.GetString(response.Body));
    }

    [Fact]
    public async Task Duplicate_replay_refuses_hostile_persisted_body_above_configured_limit()
    {
        var response = await ReplayAsync(
            new ReplayResponse(200, new Dictionary<string, string[]>(), new byte[33]),
            new ContractOptions(ReplayBodyLimitBytes: 32));

        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        Assert.NotEqual(33, response.Body.Length);
    }

    private static IEnumerable<InvalidHeaderCase> InvalidHeaderCases()
    {
        yield return new InvalidHeaderCase(
            "count",
            new ContractOptions(
                MaximumReplayHeaderCount: 1,
                MaximumReplayHeaderValueBytes: 128,
                MaximumReplayHeadersTotalBytes: 1024),
            Response(200, new Dictionary<string, string[]>
            {
                ["ETag"] = ["safe-first"],
                ["Location"] = ["/one-over"],
            }));
        yield return new InvalidHeaderCase(
            "multibyte-value",
            new ContractOptions(
                MaximumReplayHeaderCount: 8,
                MaximumReplayHeaderValueBytes: 8,
                MaximumReplayHeadersTotalBytes: 1024),
            Response(200, new Dictionary<string, string[]>
            {
                ["ETag"] = ["safe"],
                ["Location"] = [Utf8Value(9)],
            }));

        var aggregateHeaders = new Dictionary<string, string[]>
        {
            ["ETag"] = ["safe-first"],
            ["Location"] = ["/aggregate"],
        };
        yield return new InvalidHeaderCase(
            "aggregate",
            new ContractOptions(
                MaximumReplayHeaderCount: 8,
                MaximumReplayHeaderValueBytes: 16,
                MaximumReplayHeadersTotalBytes: JsonSerializer.SerializeToUtf8Bytes(aggregateHeaders).Length - 1),
            Response(200, aggregateHeaders));
        yield return new InvalidHeaderCase(
            "newline",
            new ContractOptions(),
            Response(200, new Dictionary<string, string[]>
            {
                ["ETag"] = ["safe-first"],
                ["Location"] = ["/ok\r\nX-Injected: secret"],
            }));
        yield return new InvalidHeaderCase(
            "null-array",
            new ContractOptions(),
            Response(200, new Dictionary<string, string[]>
            {
                ["ETag"] = ["safe-first"],
                ["Location"] = null!,
            }));
    }

    private static async Task<ReplayResult> ReplayAsync(
        ReplayResponse persistedResponse,
        ContractOptions limits)
    {
        var identity = new OperationIdentity("tenant", "Payments.Create", "hostile-store");
        var operation = new StoredOperation(
            identity,
            OperationFingerprint.Sha256(new string('a', 64)),
            OperationState.Completed,
            null,
            Now,
            null,
            Now.AddHours(24),
            persistedResponse,
            true,
            "digest",
            0);
        var store = new CompletedStore(operation);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
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
        var aspOptions = new OperationGuardAspNetCoreOptions
        {
            ReplayBodyLimitBytes = limits.ReplayBodyLimitBytes,
            ReplayHeaders = ["Content-Type", "ETag", "Location"],
            ScopeResolver = static (httpContext, _) =>
                ValueTask.FromResult((string)httpContext.Items["scope"]!),
        };
        var coreOptions = new OperationGuardOptions { ReplayBodyLimitBytes = limits.ReplayBodyLimitBytes };
        ContractOptionsBinding.TryApplyReplayHeaderLimits(aspOptions, limits);
        ContractOptionsBinding.TryApplyReplayHeaderLimits(coreOptions, limits);
        var middleware = new OperationGuardMiddleware(
            _ => throw new InvalidOperationException("A completed duplicate must never execute."),
            aspOptions,
            coreOptions,
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

    private static ReplayResponse Response(
        int statusCode,
        IReadOnlyDictionary<string, string[]> headers) =>
        new(statusCode, headers, Encoding.UTF8.GetBytes("ok"));

    private static string Utf8Value(int byteCount)
    {
        const string multibyte = "€";
        var fullCharacters = byteCount / Encoding.UTF8.GetByteCount(multibyte);
        var remainder = byteCount % Encoding.UTF8.GetByteCount(multibyte);
        return string.Concat(Enumerable.Repeat(multibyte, fullCharacters)) + new string('a', remainder);
    }

    private sealed class CompletedStore(StoredOperation operation) : IOperationStore
    {
        public ValueTask<OperationBeginResult> TryBeginAsync(
            OperationIdentity identity,
            OperationFingerprint fingerprint,
            DateTimeOffset now,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new OperationBeginResult(OperationBeginKind.Completed, null, operation));

        public ValueTask<StoredOperation?> ReadOutcomeAsync(
            OperationIdentity identity,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<StoredOperation?>(operation);

        public ValueTask<ConditionalWriteKind> CompleteAsync(
            OperationIdentity identity,
            string ownerToken,
            ReplayResponse? response,
            bool replayBodyAvailable,
            string? responseDigest,
            DateTimeOffset retainUntil,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ConditionalWriteKind> MarkIndeterminateAsync(
            OperationIdentity identity,
            string ownerToken,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ConditionalWriteKind> ResolveIndeterminateAsync(
            OperationIdentity identity,
            long expectedRecoveryVersion,
            ReplayResponse? response,
            DateTimeOffset retainUntil,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<OperationBeginResult> AuthorizeRecoveryAttemptAsync(
            OperationIdentity identity,
            long expectedRecoveryVersion,
            DateTimeOffset now,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<CleanupResult> DeleteExpiredBatchAsync(
            DateTimeOffset now,
            int maximumCount,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ITransactionalOperationStoreSession CreateSession(
            DbConnection connection,
            DbTransaction transaction) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record InvalidHeaderCase(
        string Name,
        ContractOptions Options,
        ReplayResponse Response);

    private sealed record ReplayResult(
        int StatusCode,
        IReadOnlyDictionary<string, string[]> Headers,
        byte[] Body);
}
