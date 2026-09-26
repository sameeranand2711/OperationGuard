using System.Data.Common;
using OperationGuard.Core;
using OperationGuard.Core.Abstractions;
using OperationGuard.Core.Internal;
using OperationGuard.Core.Models;

namespace OperationGuard.PostgreSql.Stores;

public sealed class PostgreSqlOperationStore : IOperationStore
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS operation_guard_operations
        (
            identity_hash char(64) PRIMARY KEY,
            scope text NOT NULL,
            operation_name text NOT NULL,
            idempotency_key varchar(1024) NOT NULL,
            fingerprint_algorithm varchar(64) NOT NULL,
            fingerprint_version integer NOT NULL,
            fingerprint_digest varchar(2048) NOT NULL,
            state smallint NOT NULL CHECK (state IN (0, 1, 2)),
            owner_token char(32) NULL,
            created_at timestamptz NOT NULL,
            lease_expires_at timestamptz NULL,
            status_code integer NULL,
            response_headers text NULL,
            response_body bytea NULL,
            replay_body_available boolean NOT NULL,
            retain_until timestamptz NULL,
            response_digest varchar(128) NULL,
            recovery_version bigint NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_operation_guard_operations_cleanup
            ON operation_guard_operations(state, retain_until);
        """;

    private static readonly RelationalStoreSql Sql = new(
        Insert: """
            INSERT INTO operation_guard_operations
                (identity_hash, scope, operation_name, idempotency_key, fingerprint_algorithm,
                 fingerprint_version, fingerprint_digest, state, owner_token, created_at,
                 lease_expires_at, replay_body_available, recovery_version)
            VALUES
                (@identityHash, @scope, @operationName, @idempotencyKey, @fingerprintAlgorithm,
                 @fingerprintVersion, @fingerprintDigest, 0, @ownerToken, @createdAt,
                 @leaseExpiresAt, FALSE, 0)
            ON CONFLICT (identity_hash) DO NOTHING
            """,
        Select: """
            SELECT scope, operation_name, idempotency_key, fingerprint_algorithm, fingerprint_version,
                   fingerprint_digest, state, owner_token, created_at, lease_expires_at, status_code,
                   response_headers, response_body, replay_body_available, retain_until, response_digest,
                   recovery_version
            FROM operation_guard_operations
            WHERE identity_hash = @identityHash
            """,
        ReservationSelect: """
            SELECT scope, operation_name, idempotency_key, fingerprint_algorithm, fingerprint_version,
                   fingerprint_digest, state, owner_token, created_at, lease_expires_at, status_code,
                   response_headers, response_body, replay_body_available, retain_until, response_digest,
                   recovery_version
            FROM operation_guard_operations
            WHERE identity_hash = @identityHash
            """,
        Complete: """
            UPDATE operation_guard_operations
            SET state = 1, owner_token = NULL, lease_expires_at = NULL, status_code = @statusCode,
                response_headers = @responseHeaders, response_body = @responseBody,
                replay_body_available = @replayBodyAvailable, retain_until = @retainUntil,
                response_digest = @responseDigest
            WHERE identity_hash = @identityHash
              AND convert_to(scope, 'UTF8') = convert_to(@scope, 'UTF8')
              AND convert_to(operation_name, 'UTF8') = convert_to(@operationName, 'UTF8')
              AND convert_to(idempotency_key, 'UTF8') = convert_to(@idempotencyKey, 'UTF8')
              AND state = 0 AND owner_token = @ownerToken
            """,
        MarkIndeterminate: """
            UPDATE operation_guard_operations
            SET state = 2, owner_token = NULL, lease_expires_at = NULL, recovery_version = recovery_version + 1
            WHERE identity_hash = @identityHash
              AND convert_to(scope, 'UTF8') = convert_to(@scope, 'UTF8')
              AND convert_to(operation_name, 'UTF8') = convert_to(@operationName, 'UTF8')
              AND convert_to(idempotency_key, 'UTF8') = convert_to(@idempotencyKey, 'UTF8')
              AND state = 0 AND owner_token = @ownerToken
            """,
        ResolveIndeterminate: """
            UPDATE operation_guard_operations
            SET state = 1, status_code = @statusCode, response_headers = @responseHeaders,
                response_body = @responseBody, replay_body_available = @replayBodyAvailable,
                retain_until = @retainUntil, response_digest = @responseDigest,
                recovery_version = recovery_version + 1
            WHERE identity_hash = @identityHash
              AND convert_to(scope, 'UTF8') = convert_to(@scope, 'UTF8')
              AND convert_to(operation_name, 'UTF8') = convert_to(@operationName, 'UTF8')
              AND convert_to(idempotency_key, 'UTF8') = convert_to(@idempotencyKey, 'UTF8')
              AND state = 2 AND recovery_version = @expectedRecoveryVersion
            """,
        AuthorizeRecovery: """
            UPDATE operation_guard_operations
            SET state = 0, owner_token = @ownerToken, lease_expires_at = @leaseExpiresAt,
                recovery_version = recovery_version + 1
            WHERE identity_hash = @identityHash
              AND convert_to(scope, 'UTF8') = convert_to(@scope, 'UTF8')
              AND convert_to(operation_name, 'UTF8') = convert_to(@operationName, 'UTF8')
              AND convert_to(idempotency_key, 'UTF8') = convert_to(@idempotencyKey, 'UTF8')
              AND state = 2 AND recovery_version = @expectedRecoveryVersion
            """,
        DeleteExpired: """
            DELETE FROM operation_guard_operations
            WHERE ctid IN
            (
                SELECT ctid
                FROM operation_guard_operations
                WHERE state = 1 AND retain_until <= @now
                ORDER BY retain_until, identity_hash
                FOR UPDATE SKIP LOCKED
                LIMIT @maximumCount
            )
            """);

    private readonly Implementation _implementation;

    public PostgreSqlOperationStore(
        Func<DbConnection> connectionFactory,
        OperationGuardOptions? options = null)
    {
        _implementation = new Implementation(connectionFactory, options);
    }

    public ValueTask EnsureCreatedAsync(CancellationToken cancellationToken = default) =>
        _implementation.EnsureCreatedAsync(cancellationToken);

    public ValueTask<OperationBeginResult> TryBeginAsync(OperationIdentity identity, OperationFingerprint fingerprint, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken = default) =>
        _implementation.TryBeginAsync(identity, fingerprint, now, leaseDuration, cancellationToken);

    public ValueTask<StoredOperation?> ReadOutcomeAsync(OperationIdentity identity, CancellationToken cancellationToken = default) =>
        _implementation.ReadOutcomeAsync(identity, cancellationToken);

    public ValueTask<ConditionalWriteKind> CompleteAsync(OperationIdentity identity, string ownerToken, ReplayResponse? response, bool replayBodyAvailable, string? responseDigest, DateTimeOffset retainUntil, CancellationToken cancellationToken = default) =>
        _implementation.CompleteAsync(identity, ownerToken, response, replayBodyAvailable, responseDigest, retainUntil, cancellationToken);

    public ValueTask<ConditionalWriteKind> MarkIndeterminateAsync(OperationIdentity identity, string ownerToken, CancellationToken cancellationToken = default) =>
        _implementation.MarkIndeterminateAsync(identity, ownerToken, cancellationToken);

    public ValueTask<ConditionalWriteKind> ResolveIndeterminateAsync(OperationIdentity identity, long expectedRecoveryVersion, ReplayResponse? response, DateTimeOffset retainUntil, CancellationToken cancellationToken = default) =>
        _implementation.ResolveIndeterminateAsync(identity, expectedRecoveryVersion, response, retainUntil, cancellationToken);

    public ValueTask<OperationBeginResult> AuthorizeRecoveryAttemptAsync(OperationIdentity identity, long expectedRecoveryVersion, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken = default) =>
        _implementation.AuthorizeRecoveryAttemptAsync(identity, expectedRecoveryVersion, now, leaseDuration, cancellationToken);

    public ValueTask<CleanupResult> DeleteExpiredBatchAsync(DateTimeOffset now, int maximumCount, CancellationToken cancellationToken = default) =>
        _implementation.DeleteExpiredBatchAsync(now, maximumCount, cancellationToken);

    public ITransactionalOperationStoreSession CreateSession(DbConnection connection, DbTransaction transaction) =>
        _implementation.CreateSession(connection, transaction);

    private sealed class Implementation(
        Func<DbConnection> connectionFactory,
        OperationGuardOptions? options) : RelationalOperationStore(connectionFactory, Sql, options)
    {
        public ValueTask EnsureCreatedAsync(CancellationToken cancellationToken) =>
            ExecuteSchemaAsync(SchemaSql, cancellationToken);

        protected override bool IsUniqueViolation(DbException exception) =>
            string.Equals(
                exception.GetType().GetProperty("SqlState")?.GetValue(exception) as string,
                "23505",
                StringComparison.Ordinal);

        protected override async ValueTask<OperationBeginKind?> TryAcquireReservationLockAsync(
            DbConnection connection,
            DbTransaction transaction,
            OperationIdentity identity,
            OperationFingerprint fingerprint,
            CancellationToken cancellationToken)
        {
            var electionResource = ReservationLockKey.ComputeElectionResource(identity);
            await AcquireSessionAsync(connection, transaction, electionResource, cancellationToken).ConfigureAwait(false);
            try
            {
                var fingerprintResource = ReservationLockKey.ComputeFingerprintResource(identity, fingerprint);
                var fingerprintAcquired = await TryAcquireSessionAsync(
                    connection,
                    transaction,
                    fingerprintResource,
                    CancellationToken.None).ConfigureAwait(false);
                if (!fingerprintAcquired)
                {
                    return OperationBeginKind.AlreadyInProgress;
                }

                try
                {
                    var identityAcquired = await TryAcquireTransactionAsync(
                        connection,
                        transaction,
                        ReservationLockKey.ComputeResource(identity),
                        CancellationToken.None).ConfigureAwait(false);
                    if (!identityAcquired)
                    {
                        return OperationBeginKind.FingerprintMismatch;
                    }

                    if (!await TryAcquireTransactionAsync(
                            connection,
                            transaction,
                            fingerprintResource,
                            CancellationToken.None).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException(
                            "PostgreSQL did not promote the reservation fingerprint lock.");
                    }

                    return null;
                }
                finally
                {
                    await ReleaseSessionAsync(
                        connection,
                        transaction,
                        fingerprintResource,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                await ReleaseSessionAsync(
                    connection,
                    transaction,
                    electionResource,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

        private static async ValueTask<bool> TryAcquireTransactionAsync(
            DbConnection connection,
            DbTransaction transaction,
            string resource,
            CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT pg_try_advisory_xact_lock(@lockKey)";
            var lockKey = command.CreateParameter();
            lockKey.ParameterName = "@lockKey";
            lockKey.DbType = System.Data.DbType.Int64;
            lockKey.Value = ReservationLockKey.ComputeInt64(resource);
            command.Parameters.Add(lockKey);
            return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("PostgreSQL returned no advisory-lock result."));
        }

        private static async ValueTask AcquireSessionAsync(
            DbConnection connection,
            DbTransaction transaction,
            string resource,
            CancellationToken cancellationToken)
        {
            await using var command = CreateLockCommand(
                connection,
                transaction,
                "SELECT pg_advisory_lock(@lockKey)",
                resource);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async ValueTask<bool> TryAcquireSessionAsync(
            DbConnection connection,
            DbTransaction transaction,
            string resource,
            CancellationToken cancellationToken)
        {
            await using var command = CreateLockCommand(
                connection,
                transaction,
                "SELECT pg_try_advisory_lock(@lockKey)",
                resource);
            return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("PostgreSQL returned no advisory-lock result."));
        }

        private static async ValueTask ReleaseSessionAsync(
            DbConnection connection,
            DbTransaction transaction,
            string resource,
            CancellationToken cancellationToken)
        {
            await using var command = CreateLockCommand(
                connection,
                transaction,
                "SELECT pg_advisory_unlock(@lockKey)",
                resource);
            var released = (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("PostgreSQL returned no advisory-unlock result."));
            if (!released)
            {
                throw new InvalidOperationException("PostgreSQL did not release the reservation election lock.");
            }
        }

        private static DbCommand CreateLockCommand(
            DbConnection connection,
            DbTransaction transaction,
            string commandText,
            string resource)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = commandText;
            var lockKey = command.CreateParameter();
            lockKey.ParameterName = "@lockKey";
            lockKey.DbType = System.Data.DbType.Int64;
            lockKey.Value = ReservationLockKey.ComputeInt64(resource);
            command.Parameters.Add(lockKey);
            return command;
        }
    }
}
