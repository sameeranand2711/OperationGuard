using System.Data.Common;
using OperationGuard.Core.Abstractions;
using OperationGuard.Core.Internal;
using OperationGuard.Core.Models;

namespace OperationGuard.Core.Testing;

/// <summary>Single-process test and sample store. It is not safe for multi-instance production use.</summary>
public sealed class InMemoryOperationStore : IOperationStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, StoredOperation> _operations = new(StringComparer.Ordinal);
    private bool _available = true;

    public bool Available
    {
        get => _available;
        set => _available = value;
    }

    public ValueTask<OperationBeginResult> TryBeginAsync(
        OperationIdentity identity,
        OperationFingerprint fingerprint,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAvailable();
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        lock (_sync)
        {
            var key = IdentityStorageKey.Compute(identity);
            if (_operations.TryGetValue(key, out var existing))
            {
                EnsureIdentity(existing.Identity, identity);
                if (existing.Fingerprint != fingerprint)
                {
                    return ValueTask.FromResult(new OperationBeginResult(OperationBeginKind.FingerprintMismatch, null, existing));
                }

                var kind = existing.State switch
                {
                    OperationState.InProgress => OperationBeginKind.AlreadyInProgress,
                    OperationState.Completed => OperationBeginKind.Completed,
                    OperationState.Indeterminate => OperationBeginKind.Indeterminate,
                    _ => throw new InvalidOperationException("Unknown operation state."),
                };
                return ValueTask.FromResult(new OperationBeginResult(kind, null, existing));
            }

            var ownerToken = Guid.NewGuid().ToString("N");
            var created = new StoredOperation(
                identity,
                fingerprint,
                OperationState.InProgress,
                ownerToken,
                now,
                now.Add(leaseDuration),
                null,
                null,
                false,
                null,
                0);
            _operations.Add(key, created);
            return ValueTask.FromResult(new OperationBeginResult(OperationBeginKind.Acquired, ownerToken, created));
        }
    }

    public ValueTask<StoredOperation?> ReadOutcomeAsync(OperationIdentity identity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAvailable();
        lock (_sync)
        {
            _operations.TryGetValue(IdentityStorageKey.Compute(identity), out var operation);
            if (operation is not null)
            {
                EnsureIdentity(operation.Identity, identity);
            }

            return ValueTask.FromResult(operation);
        }
    }

    public ValueTask<ConditionalWriteKind> CompleteAsync(
        OperationIdentity identity,
        string ownerToken,
        ReplayResponse? response,
        bool replayBodyAvailable,
        string? responseDigest,
        DateTimeOffset retainUntil,
        CancellationToken cancellationToken = default) =>
        UpdateOwnedAsync(identity, ownerToken, operation => operation with
        {
            State = OperationState.Completed,
            OwnerToken = null,
            LeaseExpiresAt = null,
            RetainUntil = retainUntil,
            Response = response,
            ReplayBodyAvailable = replayBodyAvailable,
            ResponseDigest = responseDigest,
        }, cancellationToken);

    public ValueTask<ConditionalWriteKind> MarkIndeterminateAsync(
        OperationIdentity identity,
        string ownerToken,
        CancellationToken cancellationToken = default) =>
        UpdateOwnedAsync(identity, ownerToken, operation => operation with
        {
            State = OperationState.Indeterminate,
            OwnerToken = null,
            LeaseExpiresAt = null,
            RecoveryVersion = operation.RecoveryVersion + 1,
        }, cancellationToken);

    public ValueTask<ConditionalWriteKind> ResolveIndeterminateAsync(
        OperationIdentity identity,
        long expectedRecoveryVersion,
        ReplayResponse? response,
        DateTimeOffset retainUntil,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAvailable();
        lock (_sync)
        {
            var key = IdentityStorageKey.Compute(identity);
            if (!_operations.TryGetValue(key, out var operation))
            {
                return ValueTask.FromResult(ConditionalWriteKind.NotFound);
            }

            if (operation.State != OperationState.Indeterminate || operation.RecoveryVersion != expectedRecoveryVersion)
            {
                return ValueTask.FromResult(ConditionalWriteKind.InvalidState);
            }

            _operations[key] = operation with
            {
                State = OperationState.Completed,
                RetainUntil = retainUntil,
                Response = response,
                ReplayBodyAvailable = response?.Body is not null,
                RecoveryVersion = operation.RecoveryVersion + 1,
            };
            return ValueTask.FromResult(ConditionalWriteKind.Applied);
        }
    }

    public ValueTask<OperationBeginResult> AuthorizeRecoveryAttemptAsync(
        OperationIdentity identity,
        long expectedRecoveryVersion,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAvailable();
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        lock (_sync)
        {
            var key = IdentityStorageKey.Compute(identity);
            if (!_operations.TryGetValue(key, out var operation))
            {
                return ValueTask.FromResult(new OperationBeginResult(OperationBeginKind.Indeterminate, null, null));
            }

            if (operation.State != OperationState.Indeterminate || operation.RecoveryVersion != expectedRecoveryVersion)
            {
                return ValueTask.FromResult(new OperationBeginResult(ToBeginKind(operation.State), null, operation));
            }

            var owner = Guid.NewGuid().ToString("N");
            operation = operation with
            {
                State = OperationState.InProgress,
                OwnerToken = owner,
                LeaseExpiresAt = now.Add(leaseDuration),
                RecoveryVersion = operation.RecoveryVersion + 1,
            };
            _operations[key] = operation;
            return ValueTask.FromResult(new OperationBeginResult(OperationBeginKind.Acquired, owner, operation));
        }
    }

    public ValueTask<CleanupResult> DeleteExpiredBatchAsync(
        DateTimeOffset now,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAvailable();
        if (maximumCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        lock (_sync)
        {
            var keys = _operations
                .Where(pair => pair.Value.State == OperationState.Completed && pair.Value.RetainUntil <= now)
                .OrderBy(pair => pair.Value.RetainUntil)
                .Take(maximumCount)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in keys)
            {
                _operations.Remove(key);
            }

            return ValueTask.FromResult(new CleanupResult(keys.Length));
        }
    }

    public ITransactionalOperationStoreSession CreateSession(DbConnection connection, DbTransaction transaction) =>
        throw new NotSupportedException("The single-process test store does not provide ADO.NET transaction enlistment.");

    public void Reset()
    {
        lock (_sync)
        {
            _operations.Clear();
            _available = true;
        }
    }

    private ValueTask<ConditionalWriteKind> UpdateOwnedAsync(
        OperationIdentity identity,
        string ownerToken,
        Func<StoredOperation, StoredOperation> update,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAvailable();
        lock (_sync)
        {
            var key = IdentityStorageKey.Compute(identity);
            if (!_operations.TryGetValue(key, out var operation))
            {
                return ValueTask.FromResult(ConditionalWriteKind.NotFound);
            }

            if (operation.State != OperationState.InProgress)
            {
                return ValueTask.FromResult(ConditionalWriteKind.InvalidState);
            }

            if (!string.Equals(operation.OwnerToken, ownerToken, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(ConditionalWriteKind.StaleOwner);
            }

            _operations[key] = update(operation);
            return ValueTask.FromResult(ConditionalWriteKind.Applied);
        }
    }

    private void EnsureAvailable()
    {
        if (!_available)
        {
            throw new InvalidOperationException("The operation protection store is unavailable.");
        }
    }

    private static void EnsureIdentity(OperationIdentity stored, OperationIdentity requested)
    {
        if (stored != requested)
        {
            throw new InvalidOperationException("An operation identity hash collision was detected.");
        }
    }

    private static OperationBeginKind ToBeginKind(OperationState state) => state switch
    {
        OperationState.InProgress => OperationBeginKind.AlreadyInProgress,
        OperationState.Completed => OperationBeginKind.Completed,
        OperationState.Indeterminate => OperationBeginKind.Indeterminate,
        _ => throw new InvalidOperationException("Unknown operation state."),
    };
}
