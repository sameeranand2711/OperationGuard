using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using OperationGuard.Core.Models;

namespace OperationGuard.Core.Internal;

internal static class ReservationLockKey
{
    private const string NamespacePrefix = "OperationGuard:v1:";

    public static string ComputeResource(OperationIdentity identity) =>
        NamespacePrefix + IdentityStorageKey.Compute(identity);

    public static long ComputeInt64(OperationIdentity identity)
    {
        var resourceBytes = Encoding.UTF8.GetBytes(ComputeResource(identity));
        var digest = SHA256.HashData(resourceBytes);
        return BinaryPrimitives.ReadInt64BigEndian(digest);
    }
}
