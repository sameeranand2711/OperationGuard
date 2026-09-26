using OperationGuard.Core.Models;

namespace OperationGuard.Core.Abstractions;

public interface IRequestFingerprintProvider
{
    ValueTask<OperationFingerprint> CreateAsync(
        FingerprintInput input,
        CancellationToken cancellationToken = default);
}
