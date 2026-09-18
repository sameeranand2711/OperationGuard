namespace OperationGuard.Core.Models;

public sealed record StoredOperation(
    OperationIdentity Identity,
    OperationFingerprint Fingerprint,
    OperationState State,
    string? OwnerToken,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LeaseExpiresAt,
    DateTimeOffset? RetainUntil,
    ReplayResponse? Response,
    bool ReplayBodyAvailable,
    string? ResponseDigest,
    long RecoveryVersion);
