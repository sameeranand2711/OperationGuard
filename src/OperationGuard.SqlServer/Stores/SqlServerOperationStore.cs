using System.Data.Common;
using OperationGuard.Core;
using OperationGuard.Core.Abstractions;
using OperationGuard.Core.Internal;
using OperationGuard.Core.Models;

namespace OperationGuard.SqlServer.Stores;

public sealed class SqlServerOperationStore : IOperationStore
{
    private const string SchemaSql = """
        IF OBJECT_ID(N'dbo.OperationGuardOperations', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.OperationGuardOperations
            (
                IdentityHash char(64) NOT NULL CONSTRAINT PK_OperationGuardOperations PRIMARY KEY,
                Scope nvarchar(max) NOT NULL,
                OperationName nvarchar(max) NOT NULL,
                IdempotencyKey nvarchar(1024) NOT NULL,
                FingerprintAlgorithm nvarchar(64) NOT NULL,
                FingerprintVersion int NOT NULL,
                FingerprintDigest nvarchar(2048) NOT NULL,
                State smallint NOT NULL,
                OwnerToken char(32) NULL,
                CreatedAt datetimeoffset(7) NOT NULL,
                LeaseExpiresAt datetimeoffset(7) NULL,
                StatusCode int NULL,
                ResponseHeaders nvarchar(max) NULL,
                ResponseBody varbinary(max) NULL,
                ReplayBodyAvailable bit NOT NULL,
                RetainUntil datetimeoffset(7) NULL,
                ResponseDigest nvarchar(128) NULL,
                RecoveryVersion bigint NOT NULL,
                CONSTRAINT CK_OperationGuardOperations_State CHECK (State IN (0, 1, 2))
            );
        END

        IF NOT EXISTS
        (
            SELECT 1
            FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'dbo.OperationGuardOperations', N'U')
              AND name = N'IX_OperationGuardOperations_Cleanup'
        )
            CREATE INDEX IX_OperationGuardOperations_Cleanup
                ON dbo.OperationGuardOperations(State, RetainUntil);
        """;

    private static readonly RelationalStoreSql Sql = new(
        Insert: """
            INSERT INTO dbo.OperationGuardOperations
                (IdentityHash, Scope, OperationName, IdempotencyKey, FingerprintAlgorithm,
                 FingerprintVersion, FingerprintDigest, State, OwnerToken, CreatedAt,
                 LeaseExpiresAt, ReplayBodyAvailable, RecoveryVersion)
            VALUES
            (
                @identityHash, @scope, @operationName, @idempotencyKey, @fingerprintAlgorithm,
                @fingerprintVersion, @fingerprintDigest, 0, @ownerToken, @createdAt,
                @leaseExpiresAt, 0, 0
            )
            """,
        Select: """
            SELECT Scope, OperationName, IdempotencyKey, FingerprintAlgorithm, FingerprintVersion,
                   FingerprintDigest, State, OwnerToken, CreatedAt, LeaseExpiresAt, StatusCode,
                   ResponseHeaders, ResponseBody, ReplayBodyAvailable, RetainUntil, ResponseDigest,
                   RecoveryVersion
            FROM dbo.OperationGuardOperations
            WHERE IdentityHash = @identityHash
            """,
        ReservationSelect: """
            SELECT Scope, OperationName, IdempotencyKey, FingerprintAlgorithm, FingerprintVersion,
                   FingerprintDigest, State, OwnerToken, CreatedAt, LeaseExpiresAt, StatusCode,
                   ResponseHeaders, ResponseBody, ReplayBodyAvailable, RetainUntil, ResponseDigest,
                   RecoveryVersion
            FROM dbo.OperationGuardOperations WITH (NOLOCK)
            WHERE IdentityHash = @identityHash
            """,
        Complete: """
            UPDATE dbo.OperationGuardOperations
            SET State = 1, OwnerToken = NULL, LeaseExpiresAt = NULL, StatusCode = @statusCode,
                ResponseHeaders = @responseHeaders, ResponseBody = @responseBody,
                ReplayBodyAvailable = @replayBodyAvailable, RetainUntil = @retainUntil,
                ResponseDigest = @responseDigest
            WHERE IdentityHash = @identityHash
              AND CONVERT(varbinary(max), Scope) = CONVERT(varbinary(max), @scope)
              AND CONVERT(varbinary(max), OperationName) = CONVERT(varbinary(max), @operationName)
              AND CONVERT(varbinary(max), IdempotencyKey) = CONVERT(varbinary(max), @idempotencyKey)
              AND State = 0 AND OwnerToken = @ownerToken
            """,
        MarkIndeterminate: """
            UPDATE dbo.OperationGuardOperations
            SET State = 2, OwnerToken = NULL, LeaseExpiresAt = NULL, RecoveryVersion = RecoveryVersion + 1
            WHERE IdentityHash = @identityHash
              AND CONVERT(varbinary(max), Scope) = CONVERT(varbinary(max), @scope)
              AND CONVERT(varbinary(max), OperationName) = CONVERT(varbinary(max), @operationName)
              AND CONVERT(varbinary(max), IdempotencyKey) = CONVERT(varbinary(max), @idempotencyKey)
              AND State = 0 AND OwnerToken = @ownerToken
            """,
        ResolveIndeterminate: """
            UPDATE dbo.OperationGuardOperations
            SET State = 1, StatusCode = @statusCode, ResponseHeaders = @responseHeaders,
                ResponseBody = @responseBody, ReplayBodyAvailable = @replayBodyAvailable,
                RetainUntil = @retainUntil, ResponseDigest = @responseDigest,
                RecoveryVersion = RecoveryVersion + 1
            WHERE IdentityHash = @identityHash
              AND CONVERT(varbinary(max), Scope) = CONVERT(varbinary(max), @scope)
              AND CONVERT(varbinary(max), OperationName) = CONVERT(varbinary(max), @operationName)
              AND CONVERT(varbinary(max), IdempotencyKey) = CONVERT(varbinary(max), @idempotencyKey)
              AND State = 2 AND RecoveryVersion = @expectedRecoveryVersion
            """,
        AuthorizeRecovery: """
            UPDATE dbo.OperationGuardOperations
            SET State = 0, OwnerToken = @ownerToken, LeaseExpiresAt = @leaseExpiresAt,
                RecoveryVersion = RecoveryVersion + 1
            WHERE IdentityHash = @identityHash
              AND CONVERT(varbinary(max), Scope) = CONVERT(varbinary(max), @scope)
              AND CONVERT(varbinary(max), OperationName) = CONVERT(varbinary(max), @operationName)
              AND CONVERT(varbinary(max), IdempotencyKey) = CONVERT(varbinary(max), @idempotencyKey)
              AND State = 2 AND RecoveryVersion = @expectedRecoveryVersion
            """,
        DeleteExpired: """
            WITH expired AS
            (
                SELECT TOP (@maximumCount) IdentityHash
                FROM dbo.OperationGuardOperations WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE State = 1 AND RetainUntil <= @now
                ORDER BY RetainUntil, IdentityHash
            )
            DELETE target
            FROM dbo.OperationGuardOperations AS target
            INNER JOIN expired ON expired.IdentityHash = target.IdentityHash
            """);

    private readonly Implementation _implementation;

    public SqlServerOperationStore(
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

        protected override bool IsUniqueViolation(DbException exception)
        {
            var number = exception.GetType().GetProperty("Number")?.GetValue(exception);
            return number is 2601 or 2627;
        }

        protected override async ValueTask<OperationBeginKind?> TryAcquireReservationLockAsync(
            DbConnection connection,
            DbTransaction transaction,
            OperationIdentity identity,
            OperationFingerprint fingerprint,
            CancellationToken cancellationToken)
        {
            var electionResource = ReservationLockKey.ComputeElectionResource(identity);
            var electionAcquired = await TryAcquireAsync(
                connection,
                transaction,
                electionResource,
                "Session",
                -1,
                cancellationToken).ConfigureAwait(false);
            if (!electionAcquired)
            {
                throw new InvalidOperationException("SQL Server did not acquire the reservation election lock.");
            }

            try
            {
                var fingerprintResource = ReservationLockKey.ComputeFingerprintResource(identity, fingerprint);
                var fingerprintAcquired = await TryAcquireAsync(
                    connection,
                    transaction,
                    fingerprintResource,
                    "Session",
                    0,
                    CancellationToken.None).ConfigureAwait(false);
                if (!fingerprintAcquired)
                {
                    return OperationBeginKind.AlreadyInProgress;
                }

                try
                {
                    var identityAcquired = await TryAcquireAsync(
                        connection,
                        transaction,
                        ReservationLockKey.ComputeResource(identity),
                        "Transaction",
                        0,
                        CancellationToken.None).ConfigureAwait(false);
                    if (!identityAcquired)
                    {
                        return OperationBeginKind.FingerprintMismatch;
                    }

                    if (!await TryAcquireAsync(
                            connection,
                            transaction,
                            fingerprintResource,
                            "Transaction",
                            0,
                            CancellationToken.None).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException(
                            "SQL Server did not promote the reservation fingerprint lock.");
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

        private static async ValueTask<bool> TryAcquireAsync(
            DbConnection connection,
            DbTransaction transaction,
            string resourceValue,
            string lockOwner,
            int lockTimeout,
            CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock
                    @Resource = @resource,
                    @LockMode = 'Exclusive',
                    @LockOwner = @lockOwner,
                    @LockTimeout = @lockTimeout,
                    @DbPrincipal = 'public';
                SELECT @result;
                """;
            var resource = command.CreateParameter();
            resource.ParameterName = "@resource";
            resource.DbType = System.Data.DbType.String;
            resource.Size = 255;
            resource.Value = resourceValue;
            command.Parameters.Add(resource);
            var owner = command.CreateParameter();
            owner.ParameterName = "@lockOwner";
            owner.DbType = System.Data.DbType.String;
            owner.Size = 32;
            owner.Value = lockOwner;
            command.Parameters.Add(owner);
            var timeout = command.CreateParameter();
            timeout.ParameterName = "@lockTimeout";
            timeout.DbType = System.Data.DbType.Int32;
            timeout.Value = lockTimeout;
            command.Parameters.Add(timeout);

            var result = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            return result switch
            {
                >= 0 => true,
                -1 => false,
                -2 => throw new OperationCanceledException(cancellationToken),
                _ => throw new InvalidOperationException(
                    $"SQL Server could not arbitrate OperationGuard ownership (sp_getapplock result {result})."),
            };
        }

        private static async ValueTask ReleaseSessionAsync(
            DbConnection connection,
            DbTransaction transaction,
            string resourceValue,
            CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DECLARE @result int;
                EXEC @result = sys.sp_releaseapplock
                    @Resource = @resource,
                    @LockOwner = 'Session',
                    @DbPrincipal = 'public';
                SELECT @result;
                """;
            var resource = command.CreateParameter();
            resource.ParameterName = "@resource";
            resource.DbType = System.Data.DbType.String;
            resource.Size = 255;
            resource.Value = resourceValue;
            command.Parameters.Add(resource);
            var result = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (result < 0)
            {
                throw new InvalidOperationException(
                    $"SQL Server did not release the reservation election lock (sp_releaseapplock result {result}).");
            }
        }
    }
}
