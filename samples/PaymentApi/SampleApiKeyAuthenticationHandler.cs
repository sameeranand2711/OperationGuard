using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace PaymentApi;

// This deliberately small scheme exists only to make the sample runnable. Production applications
// should use their established identity provider and derive tenant_id from its validated claims.
internal sealed class SampleApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SampleAuthenticationSettings settings)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "PaymentApi.SampleApiKey";
    internal const string HeaderName = "X-Payment-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        StringValues values = Request.Headers[HeaderName];
        if (values.Count != 1 || string.IsNullOrEmpty(values[0]))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var suppliedValue = values[0]!;
        if (suppliedValue.Length > 1024
            || suppliedValue.Any(character => character is < '!' or > '~'))
        {
            return Task.FromResult(AuthenticateResult.Fail("The sample API credential is invalid."));
        }

        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedValue);
        var suppliedDigest = SHA256.HashData(suppliedBytes);
        var authenticated = CryptographicOperations.FixedTimeEquals(
            suppliedDigest,
            settings.ApiKeyDigest);
        CryptographicOperations.ZeroMemory(suppliedBytes);
        CryptographicOperations.ZeroMemory(suppliedDigest);
        if (!authenticated)
        {
            return Task.FromResult(AuthenticateResult.Fail("The sample API credential is invalid."));
        }

        var identity = new ClaimsIdentity(
            [new Claim("tenant_id", settings.TenantId)],
            SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
