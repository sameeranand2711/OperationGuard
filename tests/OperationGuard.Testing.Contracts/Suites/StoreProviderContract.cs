using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using OperationGuard.Testing.Contracts.Drivers;
using OperationGuard.Testing.Contracts.Models;
using Xunit;

namespace OperationGuard.Testing.Contracts.Suites;

public abstract class StoreProviderContract<TFactory>
    where TFactory : IStoreContractDriverFactory, new()
{
    private static readonly DateTimeOffset Now = new(2035, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private static readonly ContractIdentity Identity = new("tenant-a", "Payments.Create", "key-1");
    private static readonly ContractFingerprint Fingerprint = ContractFingerprint.FromMarker("fingerprint-a");

    [ProviderFact]
    public async Task First_reservation_is_atomic_and_creates_one_active_owner()
    {
        await using var driver = await CreateResetDriverAsync();

        var result = await driver.TryBeginAsync(Identity, Fingerprint, Now, Lease, TestContext.Current.CancellationToken);
        var stored = await driver.ReadOutcomeAsync(Identity, TestContext.Current.CancellationToken);

        Assert.Equal(ContractBeginKind.Acquired, result.Kind);
        Assert.False(string.IsNullOrWhiteSpace(result.OwnerToken));
        Assert.NotNull(stored);
        Assert.Equal(ContractOperationState.InProgress, stored.State);
        Assert.Equal(result.OwnerToken, stored.OwnerToken);
        Assert.Equal(Fingerprint, stored.Fingerprint);
    }

    [ProviderFact]
    public async Task Active_duplicate_with_same_fingerprint_never_acquires_another_owner()
    {
        await using var driver = await CreateResetDriverAsync();

        var first = await BeginAsync(driver, Identity, Fingerprint);
        var duplicate = await driver.TryBeginAsync(Identity, Fingerprint, Now, Lease, TestContext.Current.CancellationToken);

        Assert.Equal(ContractBeginKind.Acquired, first.Kind);
        Assert.Equal(ContractBeginKind.AlreadyInProgress, duplicate.Kind);
    }

    [ProviderFact]
    public async Task Lease_expiry_alone_does_not_make_an_active_operation_safe_to_retry()
    {
        await using var driver = await CreateResetDriverAsync();
        await driver.TryBeginAsync(
            Identity, Fingerprint, Now, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        var duplicate = await driver.TryBeginAsync(
            Identity, Fingerprint, Now.AddHours(1), Lease, TestContext.Current.CancellationToken);

        Assert.Equal(ContractBeginKind.AlreadyInProgress, duplicate.Kind);
    }

    [ProviderFact]
    public async Task Same_identity_with_different_fingerprint_is_always_a_mismatch()
    {
        await using var driver = await CreateResetDriverAsync();
        await BeginAsync(driver, Identity, Fingerprint);

        var collision = await driver.TryBeginAsync(
            Identity,
            ContractFingerprint.FromMarker("fingerprint-b"),
            Now,
            Lease,
            TestContext.Current.CancellationToken);

        Assert.Equal(ContractBeginKind.FingerprintMismatch, collision.Kind);
        var stored = await driver.ReadOutcomeAsync(Identity, TestContext.Current.CancellationToken);
        Assert.Equal(Fingerprint, stored?.Fingerprint);
    }

    [ProviderFact]
    public async Task Same_client_key_is_isolated_by_scope_and_operation_name()
    {
        await using var driver = await CreateResetDriverAsync();
        var otherScope = Identity with { Scope = "tenant-b" };
        var otherOperation = Identity with { OperationName = "Wallet.Withdraw" };

        var results = await Task.WhenAll(
            BeginTask(driver, Identity, Fingerprint),
            BeginTask(driver, otherScope, Fingerprint),
            BeginTask(driver, otherOperation, Fingerprint));

        Assert.All(results, result => Assert.Equal(ContractBeginKind.Acquired, result.Kind));
        Assert.Equal(3, results.Select(result => result.OwnerToken).Distinct(StringComparer.Ordinal).Count());
    }

    [ProviderFact]
    public async Task Completion_is_conditional_on_the_current_owner_and_is_terminal()
    {
        await using var driver = await CreateResetDriverAsync();
        var begin = await BeginAsync(driver, Identity, Fingerprint);
        var response = Replay("accepted");

        var stale = await driver.CompleteAsync(
            Identity, "not-the-owner", response, true, "digest", Now + Retention, TestContext.Current.CancellationToken);
        var applied = await driver.CompleteAsync(
            Identity, begin.OwnerToken!, response, true, "digest", Now + Retention, TestContext.Current.CancellationToken);
        var duplicate = await driver.TryBeginAsync(Identity, Fingerprint, Now.AddMinutes(1), Lease, TestContext.Current.CancellationToken);

        Assert.Equal(ContractConditionalWriteKind.StaleOwner, stale);
        Assert.Equal(ContractConditionalWriteKind.Applied, applied);
        Assert.Equal(ContractBeginKind.Completed, duplicate.Kind);
        Assert.Equal(ContractOperationState.Completed, duplicate.Operation?.State);
        Assert.Equal(Encoding.UTF8.GetBytes("accepted"), duplicate.Operation?.Response?.Body);
    }

    [ProviderFact]
    public async Task Ambiguous_failure_becomes_indeterminate_and_is_not_automatically_retried()
    {
        await using var driver = await CreateResetDriverAsync();
        var begin = await BeginAsync(driver, Identity, Fingerprint);

        var marked = await driver.MarkIndeterminateAsync(
            Identity, begin.OwnerToken!, TestContext.Current.CancellationToken);
        var retry = await driver.TryBeginAsync(
            Identity, Fingerprint, Now.AddHours(1), Lease, TestContext.Current.CancellationToken);

        Assert.Equal(ContractConditionalWriteKind.Applied, marked);
        Assert.Equal(ContractBeginKind.Indeterminate, retry.Kind);
        Assert.Equal(ContractOperationState.Indeterminate, retry.Operation?.State);
    }

    [ProviderFact]
    public async Task Stale_owner_cannot_complete_after_an_explicit_recovery_claim()
    {
        await using var driver = await CreateResetDriverAsync();
        var first = await BeginAsync(driver, Identity, Fingerprint);
        await driver.MarkIndeterminateAsync(Identity, first.OwnerToken!, TestContext.Current.CancellationToken);
        var indeterminate = await driver.ReadOutcomeAsync(Identity, TestContext.Current.CancellationToken);

        var recovered = await driver.AuthorizeRecoveryAttemptAsync(
            Identity,
            indeterminate!.RecoveryVersion,
            Now.AddHours(1),
            Lease,
            TestContext.Current.CancellationToken);
        var staleCompletion = await driver.CompleteAsync(
            Identity,
            first.OwnerToken!,
            Replay("stale"),
            true,
            "stale-digest",
            Now + Retention,
            TestContext.Current.CancellationToken);
        var recoveredCompletion = await driver.CompleteAsync(
            Identity,
            recovered.OwnerToken!,
            Replay("recovered"),
            true,
            "recovered-digest",
            Now + Retention,
            TestContext.Current.CancellationToken);

        Assert.Equal(ContractBeginKind.Acquired, recovered.Kind);
        Assert.NotEqual(first.OwnerToken, recovered.OwnerToken);
        Assert.Equal(ContractConditionalWriteKind.StaleOwner, staleCompletion);
        Assert.Equal(ContractConditionalWriteKind.Applied, recoveredCompletion);
    }

    [ProviderFact]
    public async Task Indeterminate_can_be_resolved_to_completed_by_recovery_version()
    {
        await using var driver = await CreateResetDriverAsync();
        var begin = await BeginAsync(driver, Identity, Fingerprint);
        await driver.MarkIndeterminateAsync(Identity, begin.OwnerToken!, TestContext.Current.CancellationToken);
        var indeterminate = await driver.ReadOutcomeAsync(Identity, TestContext.Current.CancellationToken);

        var resolved = await driver.ResolveIndeterminateAsync(
            Identity,
            indeterminate!.RecoveryVersion,
            Replay("reconciled"),
            Now + Retention,
            TestContext.Current.CancellationToken);
        var outcome = await driver.ReadOutcomeAsync(Identity, TestContext.Current.CancellationToken);

        Assert.Equal(ContractConditionalWriteKind.Applied, resolved);
        Assert.Equal(ContractOperationState.Completed, outcome?.State);
    }

    [ProviderFact]
    public async Task Cleanup_removes_only_expired_completed_records()
    {
        await using var driver = await CreateResetDriverAsync();
        var expired = Identity with { IdempotencyKey = "expired" };
        var retained = Identity with { IdempotencyKey = "retained" };
        var active = Identity with { IdempotencyKey = "active" };
        var indeterminate = Identity with { IdempotencyKey = "indeterminate" };

        await CompleteAsync(driver, expired, Now.AddTicks(-1));
        await CompleteAsync(driver, retained, Now.AddHours(1));
        await BeginAsync(driver, active, Fingerprint);
        var ambiguous = await BeginAsync(driver, indeterminate, Fingerprint);
        await driver.MarkIndeterminateAsync(indeterminate, ambiguous.OwnerToken!, TestContext.Current.CancellationToken);

        var cleanup = await driver.DeleteExpiredBatchAsync(Now, 100, TestContext.Current.CancellationToken);

        Assert.Equal(1, cleanup.DeletedCount);
        Assert.Null(await driver.ReadOutcomeAsync(expired, TestContext.Current.CancellationToken));
        Assert.NotNull(await driver.ReadOutcomeAsync(retained, TestContext.Current.CancellationToken));
        Assert.Equal(ContractOperationState.InProgress, (await driver.ReadOutcomeAsync(active, TestContext.Current.CancellationToken))?.State);
        Assert.Equal(ContractOperationState.Indeterminate, (await driver.ReadOutcomeAsync(indeterminate, TestContext.Current.CancellationToken))?.State);
    }

    [ProviderFact]
    public async Task Cleanup_is_bounded_by_batch_size()
    {
        await using var driver = await CreateResetDriverAsync();
        await CompleteAsync(driver, Identity with { IdempotencyKey = "expired-1" }, Now.AddTicks(-1));
        await CompleteAsync(driver, Identity with { IdempotencyKey = "expired-2" }, Now.AddTicks(-1));

        var cleanup = await driver.DeleteExpiredBatchAsync(Now, 1, TestContext.Current.CancellationToken);

        Assert.Equal(1, cleanup.DeletedCount);
    }

    [ProviderFact]
    public async Task Completed_record_survives_cleanup_during_its_retention_window()
    {
        await using var driver = await CreateResetDriverAsync();
        await CompleteAsync(driver, Identity, Now + Retention);

        var cleanup = await driver.DeleteExpiredBatchAsync(
            Now.AddHours(23), 100, TestContext.Current.CancellationToken);
        var duplicate = await driver.TryBeginAsync(
            Identity, Fingerprint, Now.AddHours(23), Lease, TestContext.Current.CancellationToken);

        Assert.Equal(0, cleanup.DeletedCount);
        Assert.Equal(ContractBeginKind.Completed, duplicate.Kind);
    }

    [ProviderFact]
    public async Task Purged_completed_key_is_a_new_operation_after_retention()
    {
        await using var driver = await CreateResetDriverAsync();
        await CompleteAsync(driver, Identity, Now.AddHours(1));
        await driver.DeleteExpiredBatchAsync(
            Now.AddHours(2), 100, TestContext.Current.CancellationToken);

        var reused = await driver.TryBeginAsync(
            Identity, Fingerprint, Now.AddHours(2), Lease, TestContext.Current.CancellationToken);

        Assert.Equal(ContractBeginKind.Acquired, reused.Kind);
    }

    [ProviderFact]
    public async Task Cleanup_racing_completion_cannot_delete_the_active_operation()
    {
        await using var driver = await CreateResetDriverAsync();
        var begin = await BeginAsync(driver, Identity, Fingerprint);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = Task.Run(async () =>
        {
            await release.Task;
            return await driver.DeleteExpiredBatchAsync(
                Now.AddDays(2), 100, TestContext.Current.CancellationToken);
        });
        var completion = Task.Run(async () =>
        {
            await release.Task;
            return await driver.CompleteAsync(
                Identity,
                begin.OwnerToken!,
                Replay("done"),
                true,
                "digest",
                Now.AddDays(3),
                TestContext.Current.CancellationToken);
        });

        release.SetResult();
        var completionResult = await completion;
        await cleanup;
        var outcome = await driver.ReadOutcomeAsync(Identity, TestContext.Current.CancellationToken);

        Assert.Equal(ContractConditionalWriteKind.Applied, completionResult);
        Assert.Equal(ContractOperationState.Completed, outcome?.State);
    }

    [ProviderFact]
    public async Task One_hundred_simultaneous_same_key_reservations_have_exactly_one_owner()
    {
        await using var driver = await CreateResetDriverAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new ConcurrentBag<bool>();
        var tasks = Enumerable.Range(0, 100).Select(async _ =>
        {
            ready.Add(true);
            await release.Task;
            return await driver.TryBeginAsync(Identity, Fingerprint, Now, Lease, TestContext.Current.CancellationToken);
        }).ToArray();

        Assert.Equal(100, ready.Count);
        release.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Single(results, result => result.Kind == ContractBeginKind.Acquired);
        Assert.Equal(99, results.Count(result => result.Kind == ContractBeginKind.AlreadyInProgress));
    }

    [ProviderFact]
    public async Task Mixed_fingerprint_race_never_acquires_both_payloads()
    {
        await using var driver = await CreateResetDriverAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fingerprints = Enumerable.Range(0, 100)
            .Select(index => index % 2 == 0 ? Fingerprint : ContractFingerprint.FromMarker("fingerprint-b"))
            .ToArray();
        var tasks = fingerprints.Select(async fingerprint =>
        {
            await release.Task;
            return (fingerprint, result: await driver.TryBeginAsync(
                Identity, fingerprint, Now, Lease, TestContext.Current.CancellationToken));
        }).ToArray();

        release.SetResult();
        var results = await Task.WhenAll(tasks);
        var acquired = Assert.Single(results, item => item.result.Kind == ContractBeginKind.Acquired);

        Assert.All(
            results.Where(item => item.fingerprint != acquired.fingerprint),
            item => Assert.Equal(ContractBeginKind.FingerprintMismatch, item.result.Kind));
    }

    [ProviderFact]
    public async Task Transaction_rollback_removes_both_guard_and_business_mutation()
    {
        await using var driver = await CreateResetDriverAsync();

        await driver.ExecuteTransactionAsync(async (session, cancellationToken) =>
        {
            var begin = await session.TryBeginAsync(Identity, Fingerprint, Now, Lease, cancellationToken);
            await session.AddBusinessMutationAsync("payment-rollback", cancellationToken);
            await session.CompleteAsync(
                Identity, begin.OwnerToken!, null, false, null, Now + Retention, cancellationToken);
        }, commit: false, TestContext.Current.CancellationToken);

        Assert.Null(await driver.ReadOutcomeAsync(Identity, TestContext.Current.CancellationToken));
        Assert.Equal(0, await driver.CountBusinessMutationsAsync("payment-rollback", TestContext.Current.CancellationToken));
    }

    [ProviderFact]
    public async Task Transaction_commit_persists_guard_and_business_mutation_together()
    {
        await using var driver = await CreateResetDriverAsync();

        await driver.ExecuteTransactionAsync(async (session, cancellationToken) =>
        {
            var begin = await session.TryBeginAsync(Identity, Fingerprint, Now, Lease, cancellationToken);
            await session.AddBusinessMutationAsync("payment-commit", cancellationToken);
            await session.CompleteAsync(
                Identity, begin.OwnerToken!, null, false, null, Now + Retention, cancellationToken);
        }, commit: true, TestContext.Current.CancellationToken);

        Assert.Equal(ContractOperationState.Completed, (await driver.ReadOutcomeAsync(Identity, TestContext.Current.CancellationToken))?.State);
        Assert.Equal(1, await driver.CountBusinessMutationsAsync("payment-commit", TestContext.Current.CancellationToken));
    }

    [ProviderFact]
    public async Task Transaction_session_active_duplicate_returns_without_waiting_for_owner_commit()
    {
        await using var driver = await CreateResetDriverAsync();
        var maximumDuplicateWait = TimeSpan.FromSeconds(3);
        ContractBeginResult? duplicate = null;
        TimeSpan duplicateElapsed = default;

        await driver.ExecuteTransactionAsync(async (session, cancellationToken) =>
        {
            var first = await session.TryBeginAsync(Identity, Fingerprint, Now, Lease, cancellationToken);
            Assert.Equal(ContractBeginKind.Acquired, first.Kind);

            using var duplicateDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            duplicateDeadline.CancelAfter(maximumDuplicateWait);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                duplicate = await driver.TryBeginAsync(
                    Identity,
                    Fingerprint,
                    Now,
                    Lease,
                    duplicateDeadline.Token);
            }
            catch (Exception exception) when (
                duplicateDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                throw new TimeoutException(
                    $"The active duplicate waited {stopwatch.Elapsed.TotalMilliseconds:F0} ms for the owner transaction instead of returning immediately.",
                    exception);
            }

            stopwatch.Stop();
            duplicateElapsed = stopwatch.Elapsed;
            Assert.False(
                duplicateDeadline.IsCancellationRequested,
                $"The active duplicate exceeded the {maximumDuplicateWait.TotalMilliseconds:F0} ms response bound (elapsed {duplicateElapsed.TotalMilliseconds:F0} ms).");
            Assert.Equal(ContractBeginKind.AlreadyInProgress, duplicate.Kind);

            var completed = await session.CompleteAsync(
                Identity,
                first.OwnerToken!,
                Replay("committed"),
                true,
                "committed-digest",
                Now + Retention,
                cancellationToken);
            Assert.Equal(ContractConditionalWriteKind.Applied, completed);
        }, commit: true, TestContext.Current.CancellationToken);

        Assert.NotNull(duplicate);
        Assert.True(
            duplicateElapsed < maximumDuplicateWait,
            $"The active duplicate took {duplicateElapsed.TotalMilliseconds:F0} ms to return.");

        var afterCommit = await driver.TryBeginAsync(
            Identity,
            Fingerprint,
            Now.AddMinutes(1),
            Lease,
            TestContext.Current.CancellationToken);
        Assert.Equal(ContractBeginKind.Completed, afterCommit.Kind);
        Assert.Equal(ContractOperationState.Completed, afterCommit.Operation?.State);
    }

    private static ContractReplayResponse Replay(string body) => new(
        201,
        new Dictionary<string, string[]> { ["Content-Type"] = ["application/json"] },
        Encoding.UTF8.GetBytes(body));

    private static async Task CompleteAsync(
        IStoreContractDriver driver,
        ContractIdentity identity,
        DateTimeOffset retainUntil)
    {
        var begin = await BeginAsync(driver, identity, Fingerprint);
        var completed = await driver.CompleteAsync(
            identity,
            begin.OwnerToken!,
            Replay("done"),
            true,
            "digest",
            retainUntil,
            TestContext.Current.CancellationToken);
        Assert.Equal(ContractConditionalWriteKind.Applied, completed);
    }

    private static ValueTask<ContractBeginResult> BeginAsync(
        IStoreContractDriver driver,
        ContractIdentity identity,
        ContractFingerprint fingerprint) =>
        driver.TryBeginAsync(identity, fingerprint, Now, Lease, TestContext.Current.CancellationToken);

    private static async Task<ContractBeginResult> BeginTask(
        IStoreContractDriver driver,
        ContractIdentity identity,
        ContractFingerprint fingerprint) =>
        await BeginAsync(driver, identity, fingerprint);

    private static async ValueTask<IStoreContractDriver> CreateResetDriverAsync()
    {
        var driver = await new TFactory().CreateDriverAsync(TestContext.Current.CancellationToken);
        await driver.ResetAsync(TestContext.Current.CancellationToken);
        return driver;
    }
}
