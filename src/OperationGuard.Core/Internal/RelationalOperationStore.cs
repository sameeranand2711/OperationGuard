using System.Data;
using System.Data.Common;
using System.Text.Json;
using OperationGuard.Core.Abstractions;
using OperationGuard.Core.Models;

namespace OperationGuard.Core.Internal;

internal abstract class RelationalOperationStore : IOperationStore
{
    private readonly Func<DbConnection> _connectionFactory;
    private readonly RelationalStoreSql _sql;
    private readonly OperationGuardOptions _replayOptions;

    protected RelationalOperationStore(
        Func<DbConnection> connectionFactory,
        RelationalStoreSql sql,
        OperationGuardOptions? options = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _sql = sql;
        options ??= new OperationGuardOptions();
        options.Validate();
        _replayOptions = CopyReplayOptions(options);
    }

    protected abstract bool IsUniqueViolation(DbException exception);

    protected abstract ValueTask<OperationBeginKind?> TryAcquireReservationLockAsync(
        DbConnection connection,
        DbTransaction transaction,
        OperationIdentity identity,
        OperationFingerprint fingerprint,
        CancellationToken cancellationToken);

    public async ValueTask<OperationBeginResult> TryBeginAsync(
        OperationIdentity identity,
        OperationFingerprint fingerprint,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var result = await TryBeginAsync(
            connection,
            transaction,
            identity,
            fingerprint,
            now,
            leaseDuration,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<StoredOperation?> ReadOutcomeAsync(
        OperationIdentity identity,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadAsync(connection, null, identity, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ConditionalWriteKind> CompleteAsync(
        OperationIdentity identity,
        string ownerToken,
        ReplayResponse? response,
        bool replayBodyAvailable,
        string? responseDigest,
        DateTimeOffset retainUntil,
        CancellationToken cancellationToken = default)
    {
        ReplayPersistenceValidator.ValidateForPersistence(response, _replayOptions);
        await using var connection = _connectionFactory();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(
            connection,
            null,
            identity,
            ownerToken,
            response,
            replayBodyAvailable,
            responseDigest,
            retainUntil,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ConditionalWriteKind> MarkIndeterminateAsync(
        OperationIdentity identity,
        string ownerToken,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await MarkIndeterminateAsync(connection, null, identity, ownerToken, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ConditionalWriteKind> ResolveIndeterminateAsync(
        OperationIdentity identity,
        long expectedRecoveryVersion,
        ReplayResponse? response,
        DateTimeOffset retainUntil,
        CancellationToken cancellationToken = default)
    {
        ReplayPersistenceValidator.ValidateForPersistence(response, _replayOptions);
        await using var connection = _connectionFactory();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, _sql.ResolveIndeterminate);
        AddIdentity(command, identity);
        Add(command, "@expectedRecoveryVersion", expectedRecoveryVersion);
        Add(command, "@retainUntil", retainUntil);
        AddResponseParameters(command, response, response?.Body is not null, responseDigest: null);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
        {
            return ConditionalWriteKind.Applied;
        }

        var current = await ReadAsync(connection, null, identity, cancellationToken).ConfigureAwait(false);
        return current is null ? ConditionalWriteKind.NotFound : ConditionalWriteKind.InvalidState;
    }

    public async ValueTask<OperationBeginResult> AuthorizeRecoveryAttemptAsync(
        OperationIdentity identity,
        long expectedRecoveryVersion,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        await using var connection = _connectionFactory();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var owner = Guid.NewGuid().ToString("N");
        await using var command = CreateCommand(connection, null, _sql.AuthorizeRecovery);
        AddIdentity(command, identity);
        Add(command, "@expectedRecoveryVersion", expectedRecoveryVersion);
        AddOwnerToken(command, owner);
        Add(command, "@leaseExpiresAt", now.Add(leaseDuration));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
        {
            var acquired = await ReadAsync(connection, null, identity, cancellationToken).ConfigureAwait(false);
            return new OperationBeginResult(OperationBeginKind.Acquired, owner, acquired);
        }

        var current = await ReadAsync(connection, null, identity, cancellationToken).ConfigureAwait(false);
        return new OperationBeginResult(current is null ? OperationBeginKind.Indeterminate : ToBeginKind(current.State), null, current);
    }

    public async ValueTask<CleanupResult> DeleteExpiredBatchAsync(
        DateTimeOffset now,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await using var connection = _connectionFactory();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, _sql.DeleteExpired);
        Add(command, "@now", now);
        Add(command, "@maximumCount", maximumCount);
        return new CleanupResult(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    public ITransactionalOperationStoreSession CreateSession(DbConnection connection, DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(connection, transaction.Connection))
        {
            throw new ArgumentException("The transaction must belong to the supplied connection.", nameof(transaction));
        }

        return new Session(this, connection, transaction);
    }

    protected async ValueTask ExecuteSchemaAsync(string schemaSql, CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = schemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<OperationBeginResult> TryBeginAsync(
        DbConnection connection,
        DbTransaction transaction,
        OperationIdentity identity,
        OperationFingerprint fingerprint,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        var visible = await ReadAsync(
            connection,
            transaction,
            identity,
            cancellationToken,
            _sql.ReservationSelect).ConfigureAwait(false);
        if (visible is not null)
        {
            return ClassifyConflict(visible, fingerprint);
        }

        var arbitration = await TryAcquireReservationLockAsync(
                connection,
                transaction,
                identity,
                fingerprint,
                cancellationToken).ConfigureAwait(false);
        if (arbitration is not null)
        {
            return new OperationBeginResult(arbitration.Value, null, null);
        }

        var owner = Guid.NewGuid().ToString("N");
        await using var command = CreateCommand(connection, transaction, _sql.Insert);
        AddIdentity(command, identity);
        AddFingerprint(command, fingerprint);
        AddOwnerToken(command, owner);
        Add(command, "@createdAt", now);
        Add(command, "@leaseExpiresAt", now.Add(leaseDuration));
        try
        {
            var inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (inserted == 0)
            {
                return await ReadConflictAsync(
                    connection,
                    transaction,
                    identity,
                    fingerprint,
                    cancellationToken).ConfigureAwait(false);
            }

            var created = new StoredOperation(
                identity,
                fingerprint,
                OperationState.InProgress,
                owner,
                now,
                now.Add(leaseDuration),
                null,
                null,
                false,
                null,
                0);
            return new OperationBeginResult(OperationBeginKind.Acquired, owner, created);
        }
        catch (DbException exception) when (IsUniqueViolation(exception))
        {
            return await ReadConflictAsync(
                connection,
                transaction,
                identity,
                fingerprint,
                cancellationToken,
                exception).ConfigureAwait(false);
        }
    }

    private async ValueTask<OperationBeginResult> ReadConflictAsync(
        DbConnection connection,
        DbTransaction? transaction,
        OperationIdentity identity,
        OperationFingerprint fingerprint,
        CancellationToken cancellationToken,
        Exception? innerException = null)
    {
        var current = await ReadAsync(connection, transaction, identity, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The conflicting operation disappeared before it could be read.", innerException);
        return ClassifyConflict(current, fingerprint);
    }

    private static OperationBeginResult ClassifyConflict(
        StoredOperation current,
        OperationFingerprint fingerprint) =>
        current.Fingerprint != fingerprint
            ? new OperationBeginResult(OperationBeginKind.FingerprintMismatch, null, current)
            : new OperationBeginResult(ToBeginKind(current.State), null, current);

    private async ValueTask<StoredOperation?> ReadAsync(
        DbConnection connection,
        DbTransaction? transaction,
        OperationIdentity identity,
        CancellationToken cancellationToken,
        string? selectSql = null)
    {
        await using var command = CreateCommand(connection, transaction, selectSql ?? _sql.Select);
        AddIdentityHash(command, identity);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var storedIdentity = OperationIdentity.Create(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            OperationGuardOptions.HardMaximumKeyLength);
        if (storedIdentity != identity)
        {
            throw new InvalidOperationException("An operation identity hash collision was detected.");
        }

        var headersJson = reader.IsDBNull(11) ? null : reader.GetString(11);
        var body = reader.IsDBNull(12) ? null : (byte[])reader.GetValue(12);
        ReplayResponse? response = null;
        if (!reader.IsDBNull(10))
        {
            response = new ReplayResponse(
                reader.GetInt32(10),
                headersJson is null
                    ? new Dictionary<string, string[]>()
                    : JsonSerializer.Deserialize<Dictionary<string, string[]>>(headersJson)!,
                body);
        }

        return new StoredOperation(
            storedIdentity,
            new OperationFingerprint(reader.GetString(3), reader.GetInt32(4), reader.GetString(5)),
            (OperationState)reader.GetInt16(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            ReadInstant(reader, 8),
            reader.IsDBNull(9) ? null : ReadInstant(reader, 9),
            reader.IsDBNull(14) ? null : ReadInstant(reader, 14),
            response,
            reader.GetBoolean(13),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.GetInt64(16));
    }

    private async ValueTask<ConditionalWriteKind> CompleteAsync(
        DbConnection connection,
        DbTransaction? transaction,
        OperationIdentity identity,
        string ownerToken,
        ReplayResponse? response,
        bool replayBodyAvailable,
        string? responseDigest,
        DateTimeOffset retainUntil,
        CancellationToken cancellationToken)
    {
        ReplayPersistenceValidator.ValidateForPersistence(response, _replayOptions);
        await using var command = CreateCommand(connection, transaction, _sql.Complete);
        AddIdentity(command, identity);
        AddOwnerToken(command, ownerToken);
        Add(command, "@retainUntil", retainUntil);
        AddResponseParameters(command, response, replayBodyAvailable, responseDigest);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
        {
            return ConditionalWriteKind.Applied;
        }

        return await ClassifyOwnedWriteFailureAsync(connection, transaction, identity, ownerToken, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ConditionalWriteKind> MarkIndeterminateAsync(
        DbConnection connection,
        DbTransaction? transaction,
        OperationIdentity identity,
        string ownerToken,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, _sql.MarkIndeterminate);
        AddIdentity(command, identity);
        AddOwnerToken(command, ownerToken);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
        {
            return ConditionalWriteKind.Applied;
        }

        return await ClassifyOwnedWriteFailureAsync(connection, transaction, identity, ownerToken, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ConditionalWriteKind> ClassifyOwnedWriteFailureAsync(
        DbConnection connection,
        DbTransaction? transaction,
        OperationIdentity identity,
        string ownerToken,
        CancellationToken cancellationToken)
    {
        var current = await ReadAsync(connection, transaction, identity, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return ConditionalWriteKind.NotFound;
        }

        if (current.State != OperationState.InProgress)
        {
            return ConditionalWriteKind.InvalidState;
        }

        return string.Equals(current.OwnerToken, ownerToken, StringComparison.Ordinal)
            ? ConditionalWriteKind.InvalidState
            : ConditionalWriteKind.StaleOwner;
    }

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    private static OperationGuardOptions CopyReplayOptions(OperationGuardOptions options) => new()
    {
        ReplayBodyLimitBytes = options.ReplayBodyLimitBytes,
        MaximumReplayHeaderCount = options.MaximumReplayHeaderCount,
        MaximumReplayHeaderValueBytes = options.MaximumReplayHeaderValueBytes,
        MaximumReplayHeadersTotalBytes = options.MaximumReplayHeadersTotalBytes,
    };

    private static void AddIdentity(DbCommand command, OperationIdentity identity)
    {
        AddIdentityHash(command, identity);
        Add(command, "@scope", identity.Scope);
        Add(command, "@operationName", identity.OperationName);
        Add(command, "@idempotencyKey", identity.IdempotencyKey);
    }

    private static void AddIdentityHash(DbCommand command, OperationIdentity identity) =>
        Add(
            command,
            "@identityHash",
            IdentityStorageKey.Compute(identity),
            DbType.AnsiStringFixedLength,
            size: 64);

    private static void AddOwnerToken(DbCommand command, string ownerToken) =>
        Add(command, "@ownerToken", ownerToken, DbType.AnsiStringFixedLength, size: 32);

    private static void AddFingerprint(DbCommand command, OperationFingerprint fingerprint)
    {
        Add(command, "@fingerprintAlgorithm", fingerprint.Algorithm);
        Add(command, "@fingerprintVersion", fingerprint.Version);
        Add(command, "@fingerprintDigest", fingerprint.Digest);
    }

    internal static void AddResponseParameters(
        DbCommand command,
        ReplayResponse? response,
        bool replayBodyAvailable,
        string? responseDigest)
    {
        Add(command, "@statusCode", response?.StatusCode, DbType.Int32);
        Add(
            command,
            "@responseHeaders",
            response is null ? null : JsonSerializer.Serialize(response.Headers),
            DbType.String,
            size: -1);
        Add(command, "@responseBody", response?.Body, DbType.Binary, size: -1);
        Add(command, "@replayBodyAvailable", replayBodyAvailable, DbType.Boolean);
        Add(command, "@responseDigest", responseDigest, DbType.String, size: 128);
    }

    private static void Add(
        DbCommand command,
        string name,
        object? value,
        DbType? dbType = null,
        int? size = null)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        if (dbType is not null)
        {
            parameter.DbType = dbType.Value;
        }

        if (size is not null)
        {
            parameter.Size = size.Value;
        }

        parameter.Value = value switch
        {
            null => DBNull.Value,
            DateTimeOffset instant => instant.UtcDateTime,
            _ => value,
        };
        command.Parameters.Add(parameter);
    }

    private static DateTimeOffset ReadInstant(DbDataReader reader, int ordinal) => reader.GetValue(ordinal) switch
    {
        DateTimeOffset value => value.ToUniversalTime(),
        DateTime value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
        var value => throw new InvalidOperationException($"Unexpected timestamp value type '{value.GetType().FullName}'."),
    };

    private static OperationBeginKind ToBeginKind(OperationState state) => state switch
    {
        OperationState.InProgress => OperationBeginKind.AlreadyInProgress,
        OperationState.Completed => OperationBeginKind.Completed,
        OperationState.Indeterminate => OperationBeginKind.Indeterminate,
        _ => throw new InvalidOperationException("Unknown operation state."),
    };

    private sealed class Session(
        RelationalOperationStore store,
        DbConnection connection,
        DbTransaction transaction) : ITransactionalOperationStoreSession
    {
        public ValueTask<OperationBeginResult> TryBeginAsync(
            OperationIdentity identity,
            OperationFingerprint fingerprint,
            DateTimeOffset now,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) =>
            store.TryBeginAsync(connection, transaction, identity, fingerprint, now, leaseDuration, cancellationToken);

        public ValueTask<ConditionalWriteKind> CompleteAsync(
            OperationIdentity identity,
            string ownerToken,
            ReplayResponse? response,
            bool replayBodyAvailable,
            string? responseDigest,
            DateTimeOffset retainUntil,
            CancellationToken cancellationToken = default) =>
            store.CompleteAsync(
                connection,
                transaction,
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
            store.MarkIndeterminateAsync(connection, transaction, identity, ownerToken, cancellationToken);
    }
}
