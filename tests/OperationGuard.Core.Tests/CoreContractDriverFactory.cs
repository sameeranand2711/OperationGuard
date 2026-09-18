using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using OperationGuard.Core;
using OperationGuard.Core.Abstractions;
using OperationGuard.Core.Fingerprinting;
using OperationGuard.Core.Models;
using OperationGuard.Testing.Contracts.Drivers;
using OperationGuard.Testing.Contracts.Models;

namespace OperationGuard.Core.Tests;

public sealed class CoreContractDriverFactory : ICoreContractDriverFactory
{
    public ICoreContractDriver CreateDriver() => new Driver();

    private sealed class Driver : ICoreContractDriver
    {
        public object CreateIdentity(string scope, string operationName, string idempotencyKey) =>
            new OperationIdentity(scope, operationName, idempotencyKey);

        public string ComputeIdentityStorageKey(ContractIdentity identity)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Append(hash, identity.Scope);
            Append(hash, identity.OperationName);
            Append(hash, identity.IdempotencyKey);
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        public void ValidateOptions(ContractOptions options)
        {
            new OperationGuardOptions
            {
                MaximumKeyLength = options.MaximumKeyLength,
                FingerprintBodyLimitBytes = options.FingerprintBodyLimitBytes,
                ReplayBodyLimitBytes = options.ReplayBodyLimitBytes,
                CompletedRetention = options.EffectiveCompletedRetention,
                InProgressStaleAfter = options.EffectiveInProgressStaleAfter,
            }.Validate();
        }

        public ContractFingerprint Fingerprint(
            string operationName,
            string method,
            string query,
            string contentType,
            byte[] body,
            IReadOnlyDictionary<string, string[]>? selectedHeaders = null)
        {
            var fingerprint = new Sha256RequestFingerprintProvider().CreateAsync(
                new FingerprintInput(operationName, method, query, contentType, body, selectedHeaders)).GetAwaiter().GetResult();
            return ToContract(fingerprint);
        }

        public async ValueTask<ContractFingerprint> FingerprintWithCustomProviderAsync(
            ContractHttpRequest request,
            IContractFingerprintProvider provider,
            CancellationToken cancellationToken)
        {
            IRequestFingerprintProvider adapter = new Adapter(provider, request);
            var fingerprint = await adapter.CreateAsync(
                new FingerprintInput(
                    request.OperationName,
                    request.Method,
                    request.Query,
                    request.ContentType,
                    request.Body,
                    request.Headers),
                cancellationToken);
            return ToContract(fingerprint);
        }

        private static ContractFingerprint ToContract(OperationFingerprint fingerprint) =>
            new(fingerprint.Algorithm, fingerprint.Version, fingerprint.Digest);

        private static void Append(IncrementalHash hash, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        private sealed class Adapter(
            IContractFingerprintProvider provider,
            ContractHttpRequest request) : IRequestFingerprintProvider
        {
            public async ValueTask<OperationFingerprint> CreateAsync(
                FingerprintInput input,
                CancellationToken cancellationToken = default)
            {
                var result = await provider.CreateAsync(request, cancellationToken);
                return new OperationFingerprint(result.Algorithm, result.Version, result.Digest);
            }
        }
    }
}
