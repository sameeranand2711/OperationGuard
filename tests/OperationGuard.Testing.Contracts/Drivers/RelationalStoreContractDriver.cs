using System.Data.Common;
using OperationGuard.Core.Abstractions;
using OperationGuard.Core.Models;
using OperationGuard.Testing.Contracts.Models;

namespace OperationGuard.Testing.Contracts.Drivers;

public sealed class RelationalStoreContractDriver : IStoreContractDriver
{
    private readonly IOperationStore _store;
    private readonly Func<DbConnection> _connectionFactory;
    private readonly Func<CancellationToken, ValueTask> _ensureCreated;
    private readonly string _resetSql;
    private readonly string _insertBusinessSql;
    private readonly string _countBusinessSql;
    private readonly string _mutateStoredIdentitySql;
    private readonly Func<
        Func<DbConnection, DbTransaction, CancellationToken, ValueTask>,
        bool,
        CancellationToken,
        ValueTask>? _efCoreTransactionExecutor;

    public RelationalStoreContractDriver(
        IOperationStore store,
        Func<DbConnection> connectionFactory,
        Func<CancellationToken, ValueTask> ensureCreated,
        string resetSql,
        string insertBusinessSql,
        string countBusinessSql,
        string mutateStoredIdentitySql,
        Func<
            Func<DbConnection, DbTransaction, CancellationToken, ValueTask>,
            bool,
            CancellationToken,
            ValueTask>? efCoreTransactionExecutor = null)
    {
        _store = store;
        _connectionFactory = connectionFactory;
        _ensureCreated = ensureCreated;
        _resetSql = resetSql;
        _insertBusinessSql = insertBusinessSql;
        _countBusinessSql = countBusinessSql;
        _mutateStoredIdentitySql = mutateStoredIdentitySql;
        _efCoreTransactionExecutor = efCoreTransactionExecutor;
    }

    public async ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        await _ensureCreated(cancellationToken);
        await using var connection = _connectionFactory();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = _resetSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<ContractBeginResult> TryBeginAsync(
        ContractIdentity identity,
        ContractFingerprint fingerprint,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken) =>
        ToContract(await _store.TryBeginAsync(ToCore(identity), ToCore(fingerprint), now, leaseDuration, cancellationToken));

    public async ValueTask<ContractStoredOperation?> ReadOutcomeAsync(
        ContractIdentity identity,
        CancellationToken cancellationToken) =>
        ToContract(await _store.ReadOutcomeAsync(ToCore(identity), cancellationToken));

    public async ValueTask<ContractConditionalWriteKind> CompleteAsync(
        ContractIdentity identity,
        string ownerToken,
        ContractReplayResponse? response,
        bool replayBodyAvailable,
        string? responseDigest,
        DateTimeOffset retainUntil,
        CancellationToken cancellationToken) =>
        (ContractConditionalWriteKind)await _store.CompleteAsync(
            ToCore(identity),
            ownerToken,
            ToCore(response),
            replayBodyAvailable,
            responseDigest,
            retainUntil,
            cancellationToken);

    public async ValueTask<ContractConditionalWriteKind> MarkIndeterminateAsync(
        ContractIdentity identity,
        string ownerToken,
        CancellationToken cancellationToken) =>
        (ContractConditionalWriteKind)await _store.MarkIndeterminateAsync(ToCore(identity), ownerToken, cancellationToken);

    public async ValueTask<ContractConditionalWriteKind> ResolveIndeterminateAsync(
        ContractIdentity identity,
        long expectedRecoveryVersion,
        ContractReplayResponse? response,
        DateTimeOffset retainUntil,
        CancellationToken cancellationToken) =>
        (ContractConditionalWriteKind)await _store.ResolveIndeterminateAsync(
            ToCore(identity), expectedRecoveryVersion, ToCore(response), retainUntil, cancellationToken);

    public async ValueTask<ContractBeginResult> AuthorizeRecoveryAttemptAsync(
        ContractIdentity identity,
        long expectedRecoveryVersion,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken) =>
        ToContract(await _store.AuthorizeRecoveryAttemptAsync(
            ToCore(identity), expectedRecoveryVersion, now, leaseDuration, cancellationToken));

    public async ValueTask<ContractCleanupResult> DeleteExpiredBatchAsync(
        DateTimeOffset now,
        int maximumCount,
        CancellationToken cancellationToken) =>
        new((await _store.DeleteExpiredBatchAsync(now, maximumCount, cancellationToken)).DeletedCount);

    public async ValueTask ExecuteTransactionAsync(
        Func<ITransactionalStoreContractSession, CancellationToken, ValueTask> action,
        bool commit,
        CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var session = new Session(
            _store.CreateSession(connection, transaction),
            connection,
            transaction,
            _insertBusinessSql);
        try
        {
            await action(session, cancellationToken);
            if (commit)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken);
            }
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public ValueTask ExecuteEfCoreTransactionAsync(
        Func<ITransactionalStoreContractSession, CancellationToken, ValueTask> action,
        bool commit,
        CancellationToken cancellationToken)
    {
        if (_efCoreTransactionExecutor is null)
        {
            throw new InvalidOperationException(
                "The provider contract factory did not bind its EF Core manual-transaction harness.");
        }

        return _efCoreTransactionExecutor(
            async (connection, transaction, token) =>
            {
                var session = new Session(
                    _store.CreateSession(connection, transaction),
                    connection,
                    transaction,
                    _insertBusinessSql);
                await action(session, token);
            },
            commit,
            cancellationToken);
    }

    public async ValueTask ExecuteCommitThenDisconnectAsync(
        ContractIdentity identity,
        ContractFingerprint fingerprint,
        string businessKey,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        DateTimeOffset retainUntil,
        CancellationToken cancellationToken)
    {
        await ExecuteTransactionAsync(
            async (session, token) =>
            {
                var begin = await session.TryBeginAsync(identity, fingerprint, now, leaseDuration, token);
                await session.AddBusinessMutationAsync(businessKey, token);
                var result = await session.CompleteAsync(
                    identity,
                    begin.OwnerToken!,
                    response: null,
                    replayBodyAvailable: false,
                    responseDigest: null,
                    retainUntil,
                    token);
                if (result != ContractConditionalWriteKind.Applied)
                {
                    throw new InvalidOperationException($"Transactional completion returned '{result}'.");
                }
            },
            commit: true,
            cancellationToken);

        throw new IOException("Injected connection loss after the transaction committed but before acknowledgement.");
    }

    public async ValueTask<int> CountBusinessMutationsAsync(
        string businessKey,
        CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = _countBusinessSql;
        Add(command, "@businessKey", businessKey);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async ValueTask MutateStoredIdentityAsync(
        ContractIdentity replacementIdentity,
        CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = _mutateStoredIdentitySql;
        Add(command, "@scope", replacementIdentity.Scope);
        Add(command, "@operationName", replacementIdentity.OperationName);
        Add(command, "@idempotencyKey", replacementIdentity.IdempotencyKey);
        AssertSingleRow(await command.ExecuteNonQueryAsync(cancellationToken));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static OperationIdentity ToCore(ContractIdentity identity) =>
        new(identity.Scope, identity.OperationName, identity.IdempotencyKey);

    private static OperationFingerprint ToCore(ContractFingerprint fingerprint) =>
        new(fingerprint.Algorithm, fingerprint.Version, fingerprint.Digest);

    private static ReplayResponse? ToCore(ContractReplayResponse? response) => response is null
        ? null
        : new ReplayResponse(response.StatusCode, response.Headers, response.Body);

    private static ContractBeginResult ToContract(OperationBeginResult result) =>
        new((ContractBeginKind)result.Kind, result.OwnerToken, ToContract(result.Operation));

    private static ContractStoredOperation? ToContract(StoredOperation? operation) => operation is null
        ? null
        : new ContractStoredOperation(
            new ContractIdentity(operation.Identity.Scope, operation.Identity.OperationName, operation.Identity.IdempotencyKey),
            new ContractFingerprint(operation.Fingerprint.Algorithm, operation.Fingerprint.Version, operation.Fingerprint.Digest),
            (ContractOperationState)operation.State,
            operation.OwnerToken,
            operation.CreatedAt,
            operation.LeaseExpiresAt,
            operation.RetainUntil,
            operation.Response is null
                ? null
                : new ContractReplayResponse(operation.Response.StatusCode, operation.Response.Headers, operation.Response.Body),
            operation.ReplayBodyAvailable,
            operation.ResponseDigest,
            operation.RecoveryVersion);

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static void AssertSingleRow(int affectedRows)
    {
        if (affectedRows != 1)
        {
            throw new InvalidOperationException($"The identity mutation harness expected one row, but changed {affectedRows}.");
        }
    }

    private sealed class Session(
        ITransactionalOperationStoreSession store,
        DbConnection connection,
        DbTransaction transaction,
        string insertBusinessSql) : ITransactionalStoreContractSession
    {
        public async ValueTask<ContractBeginResult> TryBeginAsync(
            ContractIdentity identity,
            ContractFingerprint fingerprint,
            DateTimeOffset now,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) =>
            ToContract(await store.TryBeginAsync(ToCore(identity), ToCore(fingerprint), now, leaseDuration, cancellationToken));

        public async ValueTask AddBusinessMutationAsync(string businessKey, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = insertBusinessSql;
            Add(command, "@businessKey", businessKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async ValueTask<ContractConditionalWriteKind> CompleteAsync(
            ContractIdentity identity,
            string ownerToken,
            ContractReplayResponse? response,
            bool replayBodyAvailable,
            string? responseDigest,
            DateTimeOffset retainUntil,
            CancellationToken cancellationToken) =>
            (ContractConditionalWriteKind)await store.CompleteAsync(
                ToCore(identity),
                ownerToken,
                ToCore(response),
                replayBodyAvailable,
                responseDigest,
                retainUntil,
                cancellationToken);
    }
}
