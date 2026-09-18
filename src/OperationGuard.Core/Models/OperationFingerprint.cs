namespace OperationGuard.Core.Models;

public sealed record OperationFingerprint(string Algorithm, int Version, string Digest)
{
    public static OperationFingerprint Sha256(string digest, int version = 1) =>
        new("SHA-256", version, digest);
}
