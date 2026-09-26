using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using OperationGuard.Core.Models;

namespace OperationGuard.Core.Internal;

internal static class IdentityStorageKey
{
    public static string Compute(OperationIdentity identity)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, identity.Scope);
        Append(hash, identity.OperationName);
        Append(hash, identity.IdempotencyKey);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
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
