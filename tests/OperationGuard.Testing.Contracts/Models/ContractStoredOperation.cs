namespace OperationGuard.Testing.Contracts.Models;

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
