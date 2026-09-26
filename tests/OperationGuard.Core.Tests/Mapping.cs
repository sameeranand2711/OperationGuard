using OperationGuard.Core.Models;
using OperationGuard.Testing.Contracts.Models;

namespace OperationGuard.Core.Tests;

internal static class Mapping
{
    public static OperationIdentity ToCore(ContractIdentity identity) =>
        new(identity.Scope, identity.OperationName, identity.IdempotencyKey);

    public static OperationFingerprint ToCore(ContractFingerprint fingerprint) =>
        new(fingerprint.Algorithm, fingerprint.Version, fingerprint.Digest);

    public static ContractStoredOperation? ToContract(StoredOperation? operation) => operation is null
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
}
