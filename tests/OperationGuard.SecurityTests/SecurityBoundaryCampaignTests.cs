using System.Data.Common;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OperationGuard.AspNetCore;
using OperationGuard.AspNetCore.Metadata;
using OperationGuard.Core;
using OperationGuard.Core.Abstractions;
using OperationGuard.Core.Execution;
using OperationGuard.Core.Fingerprinting;
using OperationGuard.Core.Models;
using OperationGuard.Core.Testing;
using Xunit;

namespace OperationGuard.SecurityTests;

public sealed class SecurityBoundaryCampaignTests
{
    private static readonly DateTimeOffset Now = new(2035, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly OperationFingerprint Fingerprint = OperationFingerprint.Sha256("payload-fingerprint");

    [Fact]
    public void Identity_accepts_default_key_boundary_and_rejects_oversize_or_control_characters()
    {
        var accepted = new OperationIdentity(
            "tenant-a",
            "Payments.Create",
            new string('k', OperationGuardOptions.DefaultMaximumKeyLength));

        Assert.Equal(OperationGuardOptions.DefaultMaximumKeyLength, accepted.IdempotencyKey.Length);
        Assert.Throws<ArgumentException>(() => new OperationIdentity(
            "tenant-a",
            "Payments.Create",
            new string('k', OperationGuardOptions.DefaultMaximumKeyLength + 1)));
        Assert.Throws<ArgumentException>(() => new OperationIdentity("tenant-a", "Payments.Create", "key\r\ninjected"));
        Assert.Throws<ArgumentException>(() => new OperationIdentity("tenant\0a", "Payments.Create", "key"));
        Assert.Throws<ArgumentException>(() => new OperationIdentity("tenant-a", "Payments\tCreate", "key"));
    }

    [Fact]
    public async Task Fingerprint_accepts_exact_body_limit_and_rejects_one_byte_over()
    {
        var provider = new Sha256RequestFingerprintProvider();
        var exact = new byte[OperationGuardOptions.DefaultFingerprintBodyLimitBytes];
        var oversized = new byte[OperationGuardOptions.DefaultFingerprintBodyLimitBytes + 1];

        var fingerprint = await provider.CreateAsync(Input(exact));
        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await provider.CreateAsync(Input(oversized)));

        Assert.Equal("SHA-256", fingerprint.Algorithm);
        Assert.Equal(64, fingerprint.Digest.Length);
        Assert.Contains(OperationGuardOptions.DefaultFingerprintBodyLimitBytes.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Length_prefixed_fingerprinting_does_not_confuse_malicious_header_boundaries()
    {
        var provider = new Sha256RequestFingerprintProvider();
        var first = Input(
            Encoding.UTF8.GetBytes("payload"),
            new Dictionary<string, string[]> { ["X-Business"] = ["a\r\nX-Injected: b"] });
        var second = Input(
            Encoding.UTF8.GetBytes("payload"),
            new Dictionary<string, string[]> { ["X-Business"] = ["a", "X-Injected: b"] });

        var firstFingerprint = await provider.CreateAsync(first);
        var secondFingerprint = await provider.CreateAsync(second);

        Assert.NotEqual(firstFingerprint, secondFingerprint);
    }

    [Fact]
    public async Task Http_key_and_body_limits_are_enforced_before_the_handler()
    {
        var options = new OperationGuardOptions
        {
            FingerprintBodyLimitBytes = 64,
        };
        var harness = new HttpHarness(
            new InMemoryOperationStore(),
            coreOptions: options,
            fingerprintProvider: new Sha256RequestFingerprintProvider(64));

        var accepted = await harness.SendAsync(
            "tenant-a",
            new string('k', OperationGuardOptions.DefaultMaximumKeyLength),
            new byte[64]);
        var oversizedKey = await harness.SendAsync(
            "tenant-a",
            new string('k', OperationGuardOptions.DefaultMaximumKeyLength + 1),
            []);
        var oversizedBody = await harness.SendAsync("tenant-a", "body-too-large", new byte[65]);

        Assert.Equal(200, accepted.StatusCode);
        Assert.Equal(400, oversizedKey.StatusCode);
        Assert.Equal(413, oversizedBody.StatusCode);
        Assert.Equal(1, harness.HandlerInvocations);
    }

    [Fact]
    public async Task Completed_replay_contains_only_allowlisted_non_sensitive_headers()
    {
        var harness = new HttpHarness(new InMemoryOperationStore());
        var key = "replay-header-key";

        var first = await harness.SendAsync(
            "tenant-a",
            key,
            Encoding.UTF8.GetBytes("request"),
            async context =>
            {
                context.Response.StatusCode = StatusCodes.Status201Created;
                context.Response.Headers.ContentType = "application/json";
                context.Response.Headers.ETag = "\"version-1\"";
                context.Response.Headers.Location = "/payments/123";
                context.Response.Headers.SetCookie = "session=top-secret";
                context.Response.Headers.Authorization = "Bearer top-secret";
                context.Response.Headers.Connection = "keep-alive";
                context.Response.Headers["X-Unconfigured-Secret"] = "top-secret";
                await context.Response.WriteAsync("created");
            });
        var replay = await harness.SendAsync(
            "tenant-a",
            key,
            Encoding.UTF8.GetBytes("request"),
            _ => throw new InvalidOperationException("A completed duplicate must not execute."));

        Assert.Equal(StatusCodes.Status201Created, first.StatusCode);
        Assert.Equal(StatusCodes.Status201Created, replay.StatusCode);
        Assert.Equal("created", Encoding.UTF8.GetString(replay.Body));
        Assert.Contains("Content-Type", replay.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("ETag", replay.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Location", replay.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Set-Cookie", replay.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", replay.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Connection", replay.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Unconfigured-Secret", replay.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(1, harness.HandlerInvocations);
    }

    [Fact]
    public async Task Replay_body_boundary_completes_without_persisting_an_oversized_body_or_reexecuting()
    {
        var store = new InMemoryOperationStore();
        var options = new OperationGuardOptions
        {
            ReplayBodyLimitBytes = 32,
        };
        var harness = new HttpHarness(store, coreOptions: options);
        const string key = "oversized-replay";
        var requestBody = Encoding.UTF8.GetBytes("request");

        var original = await harness.SendAsync(
            "tenant-a",
            key,
            requestBody,
            context => context.Response.WriteAsync(new string('x', 33)));
        var duplicate = await harness.SendAsync("tenant-a", key, requestBody);
        var stored = await store.ReadOutcomeAsync(new OperationIdentity("tenant-a", "Payments.Create", key));

        Assert.Equal(StatusCodes.Status200OK, original.StatusCode);
        Assert.Equal(StatusCodes.Status409Conflict, duplicate.StatusCode);
        Assert.Equal(1, harness.HandlerInvocations);
        Assert.Equal(OperationState.Completed, stored?.State);
        Assert.False(stored?.ReplayBodyAvailable);
        Assert.Null(stored?.Response?.Body);
        Assert.False(string.IsNullOrWhiteSpace(stored?.ResponseDigest));
    }

    [Fact]
    public async Task Same_key_in_different_scopes_is_never_a_collision()
    {
        var store = new InMemoryOperationStore();
        var tenantA = new OperationIdentity("tenant-a", "Payments.Create", "shared-client-key");
        var tenantB = new OperationIdentity("tenant-b", "Payments.Create", "shared-client-key");

        var first = await store.TryBeginAsync(tenantA, Fingerprint, Now, TimeSpan.FromMinutes(15));
        var second = await store.TryBeginAsync(tenantB, Fingerprint, Now, TimeSpan.FromMinutes(15));

        Assert.Equal(OperationBeginKind.Acquired, first.Kind);
        Assert.Equal(OperationBeginKind.Acquired, second.Kind);
        Assert.NotEqual(first.OwnerToken, second.OwnerToken);
        Assert.Equal(tenantA, (await store.ReadOutcomeAsync(tenantA))?.Identity);
        Assert.Equal(tenantB, (await store.ReadOutcomeAsync(tenantB))?.Identity);
    }

    [Fact]
    public async Task Unavailable_store_fails_closed_and_does_not_log_key_or_body()
    {
        const string secretKey = "raw-secret-idempotency-key";
        const string secretBody = "raw-secret-request-body";
        var store = new InMemoryOperationStore { Available = false };
        var logger = new RecordingLogger<OperationGuardMiddleware>();
        var harness = new HttpHarness(store, middlewareLogger: logger);

        var response = await harness.SendAsync(
            "tenant-a",
            secretKey,
            Encoding.UTF8.GetBytes(secretBody));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, harness.HandlerInvocations);
        Assert.NotEmpty(logger.Entries);
        Assert.DoesNotContain(secretKey, logger.CombinedText, StringComparison.Ordinal);
        Assert.DoesNotContain(secretBody, logger.CombinedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ambiguous_message_log_does_not_contain_raw_key_or_payload_fingerprint()
    {
        const string secretKey = "message-secret-key";
        const string secretFingerprint = "message-secret-payload-marker";
        var logger = new RecordingLogger<MessageOperationExecutor>();
        var store = new InMemoryOperationStore();
        var executor = new MessageOperationExecutor(
            store,
            timeProvider: new FixedTimeProvider(Now),
            logger: logger);
        var identity = new OperationIdentity("tenant-a", "ProviderCallback.Settle", secretKey);

        await Assert.ThrowsAsync<InjectedFaultException>(async () =>
            await executor.ExecuteAsync(
                identity,
                OperationFingerprint.Sha256(secretFingerprint),
                _ => throw new InjectedFaultException()));

        Assert.NotEmpty(logger.Entries);
        Assert.DoesNotContain(secretKey, logger.CombinedText, StringComparison.Ordinal);
        Assert.DoesNotContain(secretFingerprint, logger.CombinedText, StringComparison.Ordinal);
        Assert.Equal(OperationState.Indeterminate, (await store.ReadOutcomeAsync(identity))?.State);
    }

    [Fact]
    public async Task Failure_during_response_persistence_becomes_indeterminate_and_cannot_reexecute()
    {
        var backend = new InMemoryOperationStore();
        var store = new ThrowOnceOnCompleteStore(backend);
        var harness = new HttpHarness(store);
        const string key = "response-persistence-fault";
        var requestBody = Encoding.UTF8.GetBytes("request-body");

        await Assert.ThrowsAsync<InjectedFaultException>(async () =>
            await harness.SendAsync("tenant-a", key, requestBody));
        var retry = await harness.SendAsync("tenant-a", key, requestBody);
        var stored = await backend.ReadOutcomeAsync(new OperationIdentity("tenant-a", "Payments.Create", key));

        Assert.Equal(StatusCodes.Status409Conflict, retry.StatusCode);
        Assert.Equal(1, harness.HandlerInvocations);
        Assert.Equal(OperationState.Indeterminate, stored?.State);
    }

    private static FingerprintInput Input(
        byte[] body,
        IReadOnlyDictionary<string, string[]>? selectedHeaders = null) => new(
        "Payments.Create",
        "POST",
        "?currency=USD",
        "application/json",
        body,
        selectedHeaders);

    private sealed class HttpHarness
    {
        private readonly IOperationStore _store;
        private readonly OperationGuardOptions _coreOptions;
        private readonly OperationGuardAspNetCoreOptions _aspNetCoreOptions;
        private readonly IRequestFingerprintProvider _fingerprintProvider;
        private readonly ILogger<OperationGuardMiddleware> _middlewareLogger;

        public HttpHarness(
            IOperationStore store,
            OperationGuardOptions? coreOptions = null,
            IRequestFingerprintProvider? fingerprintProvider = null,
            ILogger<OperationGuardMiddleware>? middlewareLogger = null)
        {
            _store = store;
            _coreOptions = coreOptions ?? new OperationGuardOptions();
            _aspNetCoreOptions = new OperationGuardAspNetCoreOptions
            {
                ScopeResolver = static (context, _) => ValueTask.FromResult((string)context.Items["scope"]!),
            };
            _fingerprintProvider = fingerprintProvider ?? new Sha256RequestFingerprintProvider();
            _middlewareLogger = middlewareLogger ?? new RecordingLogger<OperationGuardMiddleware>();
        }

        public int HandlerInvocations { get; private set; }

        public async Task<HttpResult> SendAsync(
            string scope,
            string key,
            byte[] body,
            Func<HttpContext, Task>? handler = null)
        {
            var context = new DefaultHttpContext();
            context.Items["scope"] = scope;
            context.Request.Method = HttpMethods.Post;
            context.Request.QueryString = new QueryString("?currency=USD");
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = body.Length;
            context.Request.Body = new MemoryStream(body);
            context.Request.Headers["Idempotency-Key"] = key;
            context.Response.Body = new MemoryStream();
            context.SetEndpoint(new Endpoint(
                null,
                new EndpointMetadataCollection(new OperationGuardEndpointMetadata("Payments.Create")),
                "Payments.Create"));

            var middleware = new OperationGuardMiddleware(
                async httpContext =>
                {
                    HandlerInvocations++;
                    if (handler is not null)
                    {
                        await handler(httpContext);
                    }
                    else
                    {
                        httpContext.Response.StatusCode = StatusCodes.Status200OK;
                        httpContext.Response.ContentType = "application/json";
                        await httpContext.Response.WriteAsync("ok");
                    }
                },
                _aspNetCoreOptions,
                _coreOptions,
                new FixedTimeProvider(Now),
                _middlewareLogger);

            await middleware.InvokeAsync(context, _store, _fingerprintProvider);
            context.Response.Body.Position = 0;
            using var copy = new MemoryStream();
            await context.Response.Body.CopyToAsync(copy);
            return new HttpResult(
                context.Response.StatusCode,
                context.Response.Headers.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Select(value => value!).ToArray(),
                    StringComparer.OrdinalIgnoreCase),
                copy.ToArray());
        }
    }

    private sealed record HttpResult(
        int StatusCode,
        IReadOnlyDictionary<string, string[]> Headers,
        byte[] Body);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly object _sync = new();
        private readonly List<string> _entries = [];

        public IReadOnlyList<string> Entries
        {
            get
            {
                lock (_sync)
                {
                    return _entries.ToArray();
                }
            }
        }

        public string CombinedText => string.Join(Environment.NewLine, Entries);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var entry = $"{logLevel}|{eventId.Id}|{formatter(state, exception)}|{exception}";
            lock (_sync)
            {
                _entries.Add(entry);
            }
        }
    }

    private sealed class InjectedFaultException : Exception;

    private sealed class ThrowOnceOnCompleteStore(IOperationStore backend) : IOperationStore
    {
        private int _failurePending = 1;

        public ValueTask<OperationBeginResult> TryBeginAsync(
            OperationIdentity identity,
            OperationFingerprint fingerprint,
            DateTimeOffset now,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) =>
            backend.TryBeginAsync(identity, fingerprint, now, leaseDuration, cancellationToken);

        public ValueTask<StoredOperation?> ReadOutcomeAsync(
            OperationIdentity identity,
            CancellationToken cancellationToken = default) =>
            backend.ReadOutcomeAsync(identity, cancellationToken);

        public ValueTask<ConditionalWriteKind> CompleteAsync(
            OperationIdentity identity,
            string ownerToken,
            ReplayResponse? response,
            bool replayBodyAvailable,
            string? responseDigest,
            DateTimeOffset retainUntil,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _failurePending, 0) == 1)
            {
                throw new InjectedFaultException();
            }

            return backend.CompleteAsync(
                identity,
                ownerToken,
                response,
                replayBodyAvailable,
                responseDigest,
                retainUntil,
                cancellationToken);
        }

        public ValueTask<ConditionalWriteKind> MarkIndeterminateAsync(
            OperationIdentity identity,
            string ownerToken,
            CancellationToken cancellationToken = default) =>
            backend.MarkIndeterminateAsync(identity, ownerToken, cancellationToken);

        public ValueTask<ConditionalWriteKind> ResolveIndeterminateAsync(
            OperationIdentity identity,
            long expectedRecoveryVersion,
            ReplayResponse? response,
            DateTimeOffset retainUntil,
            CancellationToken cancellationToken = default) =>
            backend.ResolveIndeterminateAsync(
                identity,
                expectedRecoveryVersion,
                response,
                retainUntil,
                cancellationToken);

        public ValueTask<OperationBeginResult> AuthorizeRecoveryAttemptAsync(
            OperationIdentity identity,
            long expectedRecoveryVersion,
            DateTimeOffset now,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) =>
            backend.AuthorizeRecoveryAttemptAsync(
                identity,
                expectedRecoveryVersion,
                now,
                leaseDuration,
                cancellationToken);

        public ValueTask<CleanupResult> DeleteExpiredBatchAsync(
            DateTimeOffset now,
            int maximumCount,
            CancellationToken cancellationToken = default) =>
            backend.DeleteExpiredBatchAsync(now, maximumCount, cancellationToken);

        public ITransactionalOperationStoreSession CreateSession(
            DbConnection connection,
            DbTransaction transaction) => backend.CreateSession(connection, transaction);
    }
}
