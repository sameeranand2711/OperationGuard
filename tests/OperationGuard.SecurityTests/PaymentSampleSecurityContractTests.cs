using System.Reflection;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PaymentApi;
using Xunit;

namespace OperationGuard.SecurityTests;

public sealed class PaymentSampleSecurityContractTests
{
    [Fact]
    public async Task Caller_supplied_tenant_header_alone_cannot_establish_payment_scope()
    {
        var endpointType = Assembly.Load("PaymentApi").GetType("PaymentApi.PaymentEndpoint", throwOnError: true)!;
        var resolver = endpointType.GetMethod(
            "ResolveTenantScopeAsync",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The payment sample scope resolver was not found.");
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-Id"] = "attacker-selected-tenant";

        var pending = (ValueTask<string>)resolver.Invoke(null, [context, CancellationToken.None])!;

        await Assert.ThrowsAnyAsync<Exception>(async () => await pending);
    }

    [Fact]
    public async Task Authenticated_tenant_claim_establishes_scope_and_ignores_conflicting_header()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-Id"] = "attacker-selected-tenant";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tenant_id", "server-mapped-tenant")],
            authenticationType: "test"));

        var scope = await PaymentEndpoint.ResolveTenantScopeAsync(context, CancellationToken.None);

        Assert.Equal("server-mapped-tenant", scope);
    }

    [Fact]
    public async Task Sample_authentication_returns_401_for_missing_or_invalid_credentials_and_maps_valid_credential()
    {
        const string apiKey = "sample-only-credential-at-least-32-chars";
        await using var application = await StartAuthenticationHostAsync(apiKey, "configured-tenant");

        var missing = await application.Client.GetAsync("/");
        using var invalidRequest = new HttpRequestMessage(HttpMethod.Get, "/");
        invalidRequest.Headers.Add(SampleApiKeyAuthenticationHandler.HeaderName, new string('x', 40));
        var invalid = await application.Client.SendAsync(invalidRequest);
        using var validRequest = new HttpRequestMessage(HttpMethod.Get, "/");
        validRequest.Headers.Add(SampleApiKeyAuthenticationHandler.HeaderName, apiKey);
        var valid = await application.Client.SendAsync(validRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal("configured-tenant", await valid.Content.ReadAsStringAsync());
    }

    private static async Task<AuthenticationHost> StartAuthenticationHostAsync(string apiKey, string tenantId)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseKestrel()
                .UseUrls("http://127.0.0.1:0")
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton(SampleAuthenticationSettings.Create(apiKey, tenantId));
                    services
                        .AddAuthentication(SampleApiKeyAuthenticationHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, SampleApiKeyAuthenticationHandler>(
                            SampleApiKeyAuthenticationHandler.SchemeName,
                            _ => { });
                    services.AddAuthorization();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints
                        .MapGet("/", context => context.Response.WriteAsync(
                            context.User.FindFirst("tenant_id")?.Value ?? "missing"))
                        .RequireAuthorization());
                }))
            .Build();
        await host.StartAsync();
        var address = Assert.Single(host.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses);
        return new AuthenticationHost(host, new HttpClient { BaseAddress = new Uri(address) });
    }

    private sealed class AuthenticationHost(IHost host, HttpClient client) : IAsyncDisposable
    {
        internal HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await host.StopAsync();
            host.Dispose();
        }
    }
}
