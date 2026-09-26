using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using OperationGuard.Core.Models;

namespace OperationGuard.Core.Internal;

internal static class ReservationLockKey
{
    private const string NamespacePrefix = "OperationGuard:v1:";

    public static string ComputeResource(OperationIdentity identity) =>
        NamespacePrefix + "owner:" + IdentityStorageKey.Compute(identity);

    public static string ComputeElectionResource(OperationIdentity identity) =>
        NamespacePrefix + "election:" + IdentityStorageKey.Compute(identity);

    public static string ComputeFingerprintResource(
        OperationIdentity identity,
        OperationFingerprint fingerprint)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, IdentityStorageKey.Compute(identity));
        Append(hash, fingerprint.Algorithm);
        Span<byte> version = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(version, fingerprint.Version);
        hash.AppendData(version);
        Append(hash, fingerprint.Digest);
        return NamespacePrefix + "fingerprint:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static long ComputeInt64(string resource)
    {
        var resourceBytes = Encoding.UTF8.GetBytes(resource);
        var digest = SHA256.HashData(resourceBytes);
        return BinaryPrimitives.ReadInt64BigEndian(digest);
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
