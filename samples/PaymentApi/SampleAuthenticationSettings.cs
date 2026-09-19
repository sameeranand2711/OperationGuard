using System.Security.Cryptography;
using System.Text;
using OperationGuard.Core.Models;

namespace PaymentApi;

internal sealed class SampleAuthenticationSettings
{
    private const int MinimumApiKeyLength = 32;
    private const int MaximumApiKeyLength = 1024;

    private SampleAuthenticationSettings(byte[] apiKeyDigest, string tenantId)
    {
        ApiKeyDigest = apiKeyDigest;
        TenantId = tenantId;
    }

    internal byte[] ApiKeyDigest { get; }

    internal string TenantId { get; }

    internal static SampleAuthenticationSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var apiKey = configuration["SampleAuthentication:ApiKey"];
        var tenantId = configuration["SampleAuthentication:TenantId"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "Configure SampleAuthentication:ApiKey (or SampleAuthentication__ApiKey) with an uncommitted demo credential of at least 32 characters.");
        }

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new InvalidOperationException(
                "Configure SampleAuthentication:TenantId (or SampleAuthentication__TenantId) with the server-side tenant mapped to the demo credential.");
        }

        return Create(apiKey, tenantId);
    }

    internal static SampleAuthenticationSettings Create(string apiKey, string tenantId)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(tenantId);
        if (apiKey.Length is < MinimumApiKeyLength or > MaximumApiKeyLength
            || apiKey.Any(character => character is < '!' or > '~'))
        {
            throw new ArgumentException(
                $"The sample API key must contain {MinimumApiKeyLength}-{MaximumApiKeyLength} printable ASCII characters without spaces.",
                nameof(apiKey));
        }

        _ = new OperationIdentity(tenantId, "Payments.Create", "sample-configuration-validation");
        return new SampleAuthenticationSettings(
            SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)),
            tenantId);
    }
}
