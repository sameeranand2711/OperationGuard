using System.Data.Common;
using OperationGuard.Core;
using OperationGuard.Core.Abstractions;
using OperationGuard.Core.Execution;
using OperationGuard.Core.Models;
using OperationGuard.Core.Testing;
using Xunit;

namespace OperationGuard.ConcurrencyTests;

public sealed class InMemoryConcurrencyCampaignTests
{
    private static readonly DateTimeOffset Now = new(2035, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private static readonly OperationIdentity Identity = new("tenant-a", "Payments.Create", "hot-key");
    private static readonly OperationFingerprint FingerprintA = OperationFingerprint.Sha256("fingerprint-a");
    private static readonly OperationFingerprint FingerprintB = OperationFingerprint.Sha256("fingerprint-b");

    [Fact]
    public async Task One_hundred_twenty_eight_same_key_reservations_have_exactly_one_owner()
    {
        var store = new InMemoryOperationStore();

        var results = await StartTogetherAsync(
            128,
            _ => store.TryBeginAsync(Identity, FingerprintA, Now, Lease).AsTask());

        Assert.Single(results, result => result.Kind == OperationBeginKind.Acquired);
        Assert.Equal(127, results.Count(result => result.Kind == OperationBeginKind.AlreadyInProgress));
        Assert.Single(results.Where(result => result.OwnerToken is not null).Select(result => result.OwnerToken));
    }

    [Fact]
    public async Task Independent_service_and_store_handles_share_one_durable_owner_semantics()
    {
        var backend = new InMemoryOperationStore();
        var handles = Enumerable.Range(0, 32)
            .Select(_ => new SharedStoreHandle(backend))
            .ToArray();
        var executors = handles
            .Select(handle => new MessageOperationExecutor(handle, timeProvider: new FixedTimeProvider(Now)))
            .ToArray();
        var handlerInvocations = 0;

        var results = await StartTogetherAsync(
            128,
            index => executors[index % executors.Length].ExecuteAsync(
                Identity,
                FingerprintA,
                _ =>
                {
                    Interlocked.Increment(ref handlerInvocations);
                    return ValueTask.CompletedTask;
                }).AsTask());

        Assert.Equal(1, Volatile.Read(ref handlerInvocations));
        Assert.Single(results, result => result == OperationBeginKind.Acquired);
        Assert.All(
            results.Where(result => result != OperationBeginKind.Acquired),
            result => Assert.True(
                result is OperationBeginKind.AlreadyInProgress or OperationBeginKind.Completed,
                $"Unexpected duplicate result: {result}."));
        Assert.Equal(OperationState.Completed, (await backend.ReadOutcomeAsync(Identity))?.State);
        Assert.Equal(handles.Length, handles.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Equal(executors.Length, executors.Distinct(ReferenceEqualityComparer.Instance).Count());
    }

    [Fact]
    public async Task Mixed_fingerprint_hot_key_race_never_acquires_both_payloads()
    {
        var store = new InMemoryOperationStore();
        var attempts = Enumerable.Range(0, 128)
            .Select(index => index % 2 == 0 ? FingerprintA : FingerprintB)
            .ToArray();

        var results = await StartTogetherAsync(
            attempts.Length,
            async index => (Fingerprint: attempts[index], Result: await store.TryBeginAsync(
                Identity,
                attempts[index],
                Now,
                Lease)));

        var acquired = Assert.Single(results, item => item.Result.Kind == OperationBeginKind.Acquired);
        Assert.All(
            results.Where(item => item.Fingerprint != acquired.Fingerprint),
            item => Assert.Equal(OperationBeginKind.FingerprintMismatch, item.Result.Kind));
        Assert.All(
            results.Where(item => item.Fingerprint == acquired.Fingerprint && item.Result.Kind != OperationBeginKind.Acquired),
            item => Assert.Equal(OperationBeginKind.AlreadyInProgress, item.Result.Kind));
    }

    [Fact]
    public async Task Cleanup_racing_active_completion_cannot_remove_the_operation()
    {
        var store = new InMemoryOperationStore();
        var begin = await store.TryBeginAsync(Identity, FingerprintA, Now, Lease);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupTask = Task.Run(async () =>
        {
            await release.Task;
            return await store.DeleteExpiredBatchAsync(Now.AddDays(2), 100);
        });
        var completionTask = Task.Run(async () =>
        {
            await release.Task;
            return await store.CompleteAsync(
                Identity,
                begin.OwnerToken!,
                Replay("completed"),
                replayBodyAvailable: true,
                responseDigest: "digest",
                retainUntil: Now.AddDays(3));
        });

        release.SetResult();
        var completion = await completionTask;
        var cleanup = await cleanupTask;
        var stored = await store.ReadOutcomeAsync(Identity);

        Assert.Equal(ConditionalWriteKind.Applied, completion);
        Assert.Equal(0, cleanup.DeletedCount);
        Assert.Equal(OperationState.Completed, stored?.State);
    }

    [Fact]
    public async Task Stale_owner_is_fenced_after_explicit_recovery_claim()
    {
        var store = new InMemoryOperationStore();
        var original = await store.TryBeginAsync(Identity, FingerprintA, Now, Lease);
        Assert.Equal(ConditionalWriteKind.Applied, await store.MarkIndeterminateAsync(Identity, original.OwnerToken!));
        var indeterminate = await store.ReadOutcomeAsync(Identity);
        var recovered = await store.AuthorizeRecoveryAttemptAsync(
            Identity,
            indeterminate!.RecoveryVersion,
            Now.AddHours(1),
            Lease);

        var stale = await store.CompleteAsync(
            Identity,
            original.OwnerToken!,
            Replay("stale"),
            true,
            "stale-digest",
            Now + Retention);
        var current = await store.CompleteAsync(
            Identity,
            recovered.OwnerToken!,
            Replay("current"),
            true,
            "current-digest",
            Now + Retention);

        Assert.Equal(OperationBeginKind.Acquired, recovered.Kind);
        Assert.NotEqual(original.OwnerToken, recovered.OwnerToken);
        Assert.Equal(ConditionalWriteKind.StaleOwner, stale);
        Assert.Equal(ConditionalWriteKind.Applied, current);
        Assert.Equal("current", System.Text.Encoding.UTF8.GetString((await store.ReadOutcomeAsync(Identity))!.Response!.Body!));
    }

    [Fact]
    public async Task Many_independent_keys_progress_while_one_hot_key_is_contended()
    {
        var store = new InMemoryOperationStore();
        const int independentCount = 256;
        const int hotCount = 128;

        var results = await StartTogetherAsync(
            independentCount + hotCount,
            index =>
            {
                var identity = index < independentCount
                    ? new OperationIdentity(Identity.Scope, Identity.OperationName, $"independent-{index:D3}")
                    : Identity;
                return store.TryBeginAsync(identity, FingerprintA, Now, Lease).AsTask();
            });

        Assert.Equal(
            independentCount + 1,
            results.Count(result => result.Kind == OperationBeginKind.Acquired));
        Assert.Equal(
            hotCount - 1,
            results.Count(result => result.Kind == OperationBeginKind.AlreadyInProgress));
    }

    [Fact]
    public async Task Abandoned_reservation_is_not_retried_merely_because_its_lease_expired()
    {
        var store = new InMemoryOperationStore();
        var original = await store.TryBeginAsync(Identity, FingerprintA, Now, TimeSpan.FromSeconds(1));

        var retry = await store.TryBeginAsync(Identity, FingerprintA, Now.AddDays(1), Lease);

        Assert.Equal(OperationBeginKind.Acquired, original.Kind);
        Assert.Equal(OperationBeginKind.AlreadyInProgress, retry.Kind);
        Assert.Equal(original.OwnerToken, (await store.ReadOutcomeAsync(Identity))?.OwnerToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handler_failure_before_or_after_a_possible_business_effect_becomes_indeterminate(
        bool applyBusinessEffect)
    {
        var store = new InMemoryOperationStore();
        var executor = new MessageOperationExecutor(store, timeProvider: new FixedTimeProvider(Now));
        var businessEffects = 0;

        await Assert.ThrowsAsync<InjectedFaultException>(async () =>
            await executor.ExecuteAsync(
                Identity,
                FingerprintA,
                _ =>
                {
                    if (applyBusinessEffect)
                    {
                        Interlocked.Increment(ref businessEffects);
                    }

                    throw new InjectedFaultException();
                }));
        var retryHandlerInvocations = 0;
        var retry = await executor.ExecuteAsync(
            Identity,
            FingerprintA,
            _ =>
            {
                Interlocked.Increment(ref retryHandlerInvocations);
                return ValueTask.CompletedTask;
            });

        Assert.Equal(applyBusinessEffect ? 1 : 0, businessEffects);
        Assert.Equal(OperationBeginKind.Indeterminate, retry);
        Assert.Equal(0, retryHandlerInvocations);
        Assert.Equal(OperationState.Indeterminate, (await store.ReadOutcomeAsync(Identity))?.State);
    }

    [Fact]
    public async Task Completion_failure_is_marked_indeterminate_and_never_automatically_reexecutes()
    {
        var backend = new InMemoryOperationStore();
        var store = new ThrowOnceOnCompleteStore(backend);
        var executor = new MessageOperationExecutor(store, timeProvider: new FixedTimeProvider(Now));
        var handlerInvocations = 0;

        await Assert.ThrowsAsync<InjectedFaultException>(async () =>
            await executor.ExecuteAsync(
                Identity,
                FingerprintA,
                _ =>
                {
                    Interlocked.Increment(ref handlerInvocations);
                    return ValueTask.CompletedTask;
                }));
        var retry = await executor.ExecuteAsync(
            Identity,
            FingerprintA,
            _ =>
            {
                Interlocked.Increment(ref handlerInvocations);
                return ValueTask.CompletedTask;
            });

        Assert.Equal(1, handlerInvocations);
        Assert.Equal(OperationBeginKind.Indeterminate, retry);
        Assert.Equal(OperationState.Indeterminate, (await backend.ReadOutcomeAsync(Identity))?.State);
    }

    private static ReplayResponse Replay(string value) => new(
        200,
        new Dictionary<string, string[]> { ["Content-Type"] = ["text/plain"] },
        System.Text.Encoding.UTF8.GetBytes(value));

    private static async Task<T[]> StartTogetherAsync<T>(int count, Func<int, Task<T>> action)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;
        var tasks = Enumerable.Range(0, count).Select(async index =>
        {
            if (Interlocked.Increment(ref readyCount) == count)
            {
                ready.SetResult();
            }

            await release.Task;
            return await action(index);
        }).ToArray();

        await ready.Task;
        release.SetResult();
        return await Task.WhenAll(tasks);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class InjectedFaultException : Exception;

    private class SharedStoreHandle(IOperationStore backend) : IOperationStore
    {
        protected IOperationStore Backend { get; } = backend;

        public ValueTask<OperationBeginResult> TryBeginAsync(
            OperationIdentity identity,
            OperationFingerprint fingerprint,
            DateTimeOffset now,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) =>
            Backend.TryBeginAsync(identity, fingerprint, now, leaseDuration, cancellationToken);

        public ValueTask<StoredOperation?> ReadOutcomeAsync(
            OperationIdentity identity,
            CancellationToken cancellationToken = default) =>
            Backend.ReadOutcomeAsync(identity, cancellationToken);

        public virtual ValueTask<ConditionalWriteKind> CompleteAsync(
            OperationIdentity identity,
            string ownerToken,
            ReplayResponse? response,
            bool replayBodyAvailable,
            string? responseDigest,
            DateTimeOffset retainUntil,
            CancellationToken cancellationToken = default) =>
            Backend.CompleteAsync(
                identity,
                ownerToken,
                response,
                replayBodyAvailable,
                responseDigest,
                retainUntil,
                cancellationToken);

        public ValueTask<ConditionalWriteKind> MarkIndeterminateAsync(
            OperationIdentity identity,
            string ownerToken,
            CancellationToken cancellationToken = default) =>
            Backend.MarkIndeterminateAsync(identity, ownerToken, cancellationToken);

        public ValueTask<ConditionalWriteKind> ResolveIndeterminateAsync(
            OperationIdentity identity,
            long expectedRecoveryVersion,
            ReplayResponse? response,
            DateTimeOffset retainUntil,
            CancellationToken cancellationToken = default) =>
            Backend.ResolveIndeterminateAsync(
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
            Backend.AuthorizeRecoveryAttemptAsync(
                identity,
                expectedRecoveryVersion,
                now,
                leaseDuration,
                cancellationToken);

        public ValueTask<CleanupResult> DeleteExpiredBatchAsync(
            DateTimeOffset now,
            int maximumCount,
            CancellationToken cancellationToken = default) =>
            Backend.DeleteExpiredBatchAsync(now, maximumCount, cancellationToken);

        public ITransactionalOperationStoreSession CreateSession(
            DbConnection connection,
            DbTransaction transaction) => Backend.CreateSession(connection, transaction);
    }

    private sealed class ThrowOnceOnCompleteStore(IOperationStore backend) : SharedStoreHandle(backend)
    {
        private int _failurePending = 1;

        public override ValueTask<ConditionalWriteKind> CompleteAsync(
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

            return base.CompleteAsync(
                identity,
                ownerToken,
                response,
                replayBodyAvailable,
                responseDigest,
                retainUntil,
                cancellationToken);
        }
    }
}
