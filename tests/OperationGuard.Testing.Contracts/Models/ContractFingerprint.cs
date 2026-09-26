namespace OperationGuard.Testing.Contracts.Models;

public sealed record ContractFingerprint(string Algorithm, int Version, string Digest)
{
    public static ContractFingerprint FromMarker(string marker) => new("SHA-256", 1, marker);
}
