using OperationGuard.Testing.Contracts.Models;

namespace OperationGuard.Testing.Contracts.Drivers;

public interface ICoreContractDriver
{
    object CreateIdentity(string scope, string operationName, string idempotencyKey);

    string ComputeIdentityStorageKey(ContractIdentity identity);

    void ValidateOptions(ContractOptions options);

    ContractFingerprint Fingerprint(
        string operationName,
        string method,
        string query,
        string contentType,
        byte[] body,
        IReadOnlyDictionary<string, string[]>? selectedHeaders = null);

    ValueTask<ContractFingerprint> FingerprintWithCustomProviderAsync(
        ContractHttpRequest request,
        IContractFingerprintProvider provider,
        CancellationToken cancellationToken);
}
