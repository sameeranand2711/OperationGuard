using System.Security.Cryptography;
using System.Text;

namespace OperationGuard.Core.Internal;

internal static class SensitiveValueRedactor
{
    public static string HashForDiagnostics(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant();
    }
}
