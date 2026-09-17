namespace OperationGuard.Testing.Contracts.Models;

public enum ContractOperationState
{
    InProgress,
    Completed,
    Indeterminate,
}

public enum ContractBeginKind
{
    Acquired,
    AlreadyInProgress,
    Completed,
    Indeterminate,
    FingerprintMismatch,
}

public enum ContractConditionalWriteKind
{
    Applied,
    StaleOwner,
    InvalidState,
    NotFound,
}

public sealed record ContractBeginResult(
    ContractBeginKind Kind,
    string? OwnerToken,
    ContractStoredOperation? Operation);

public sealed record ContractStoredOperation(
    ContractIdentity Identity,
    ContractFingerprint Fingerprint,
    ContractOperationState State,
    string? OwnerToken,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LeaseExpiresAt,
    DateTimeOffset? RetainUntil,
    ContractReplayResponse? Response,
    bool ReplayBodyAvailable,
    string? ResponseDigest,
    long RecoveryVersion);

public sealed record ContractReplayResponse(
    int StatusCode,
    IReadOnlyDictionary<string, string[]> Headers,
    byte[]? Body);

public sealed record ContractCleanupResult(int DeletedCount);
