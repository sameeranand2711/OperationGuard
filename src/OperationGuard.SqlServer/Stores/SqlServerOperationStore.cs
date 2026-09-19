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
    }
}
