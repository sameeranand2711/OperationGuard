using System.Reflection;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OperationGuard.AspNetCore;
using OperationGuard.Core;
using OperationGuard.Core.Models;
using OperationGuard.Testing.Contracts.Drivers;
using OperationGuard.Testing.Contracts.Models;
using PaymentApi;
using Xunit;

namespace OperationGuard.SecurityTests;

public sealed class PaymentSampleSecurityContractTests
{
    private static readonly DateTimeOffset Now = new(2035, 9, 10, 11, 12, 13, TimeSpan.Zero);

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

    [Fact]
    public async Task Payment_replay_rejects_oversized_or_corrupt_stored_headers_before_adding_any_header()
    {
        foreach (var invalidCase in InvalidPaymentReplayCases())
        {
            var context = new DefaultHttpContext();
            var result = await InvokePaymentReplayAsync(context, invalidCase.Response, invalidCase.Options);

            Assert.Empty(context.Response.Headers);
            Assert.Equal(
                StatusCodes.Status409Conflict,
                Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        }
    }

    [Theory]
    [InlineData(199)]
    [InlineData(600)]
    public async Task Payment_replay_rejects_non_final_or_out_of_range_stored_status(int statusCode)
    {
        var context = new DefaultHttpContext();

        var result = await InvokePaymentReplayAsync(
            context,
            Response(statusCode, new Dictionary<string, string[]>()),
            new ContractOptions(),
            applyHeaderLimits: false);

        Assert.Empty(context.Response.Headers);
        Assert.Equal(
            StatusCodes.Status409Conflict,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task Payment_replay_accepts_exact_header_boundaries_and_filters_non_allowlisted_headers()
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = ["application/json"],
            ["Location"] = [Utf8Value(1024)],
            ["ETag"] = ["not-configured-for-this-sample"],
            ["Set-Cookie"] = ["session=secret"],
        };
        var persisted = Response(200, headers);
        var limits = new ContractOptions(
            MaximumReplayHeaderCount: headers.Count,
            MaximumReplayHeaderValueBytes: 1024,
            MaximumReplayHeadersTotalBytes: JsonSerializer.SerializeToUtf8Bytes(headers).Length);
        var context = new DefaultHttpContext();

        var result = await InvokePaymentReplayAsync(context, persisted, limits);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("application/json", Assert.Single(context.Response.Headers["Content-Type"]));
        Assert.Equal(Utf8Value(1024), Assert.Single(context.Response.Headers["Location"]));
        Assert.DoesNotContain("ETag", context.Response.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Set-Cookie", context.Response.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(
            StatusCodes.Status409Conflict,
            (result as IStatusCodeHttpResult)?.StatusCode);
    }

    private static IEnumerable<InvalidPaymentReplayCase> InvalidPaymentReplayCases()
    {
        yield return new InvalidPaymentReplayCase(
            "count",
            new ContractOptions(
                MaximumReplayHeaderCount: 1,
                MaximumReplayHeaderValueBytes: 128,
                MaximumReplayHeadersTotalBytes: 1024),
            Response(200, new Dictionary<string, string[]>
            {
                ["Content-Type"] = ["application/json"],
                ["Location"] = ["/payments/count"],
            }));
        yield return new InvalidPaymentReplayCase(
            "multibyte-value",
            new ContractOptions(
                MaximumReplayHeaderCount: 8,
                MaximumReplayHeaderValueBytes: 8,
                MaximumReplayHeadersTotalBytes: 1024),
            Response(200, new Dictionary<string, string[]>
            {
                ["Content-Type"] = ["safe-first"],
                ["Location"] = [Utf8Value(9)],
            }));

        var aggregateHeaders = new Dictionary<string, string[]>
        {
            ["Content-Type"] = ["safe-first"],
            ["Location"] = ["/aggregate"],
        };
        yield return new InvalidPaymentReplayCase(
            "aggregate",
            new ContractOptions(
                MaximumReplayHeaderCount: 8,
                MaximumReplayHeaderValueBytes: 16,
                MaximumReplayHeadersTotalBytes: JsonSerializer.SerializeToUtf8Bytes(aggregateHeaders).Length - 1),
            Response(200, aggregateHeaders));
        yield return new InvalidPaymentReplayCase(
            "newline",
            new ContractOptions(),
            Response(200, new Dictionary<string, string[]>
            {
                ["Content-Type"] = ["safe-first"],
                ["Location"] = ["/ok\r\nX-Injected: secret"],
            }));
        yield return new InvalidPaymentReplayCase(
            "null-array",
            new ContractOptions(),
            Response(200, new Dictionary<string, string[]>
            {
                ["Content-Type"] = ["safe-first"],
                ["Location"] = null!,
            }));
    }

    private static async ValueTask<IResult> InvokePaymentReplayAsync(
        HttpContext context,
        ReplayResponse response,
        ContractOptions limits,
        bool applyHeaderLimits = true)
    {
        var operation = new StoredOperation(
            new OperationIdentity("tenant", "Payments.Create", "stored-replay"),
            OperationFingerprint.Sha256(new string('a', 64)),
            OperationState.Completed,
            null,
            Now,
            null,
            Now.AddHours(24),
            response,
            true,
            "digest",
            0);
        var httpOptions = new OperationGuardAspNetCoreOptions
        {
            ReplayHeaders = ["Content-Type", "Location"],
        };
        var coreOptions = new OperationGuardOptions();
        if (applyHeaderLimits)
        {
            ContractOptionsBinding.TryApplyReplayHeaderLimits(httpOptions, limits);
            ContractOptionsBinding.TryApplyReplayHeaderLimits(coreOptions, limits);
        }

        var replay = typeof(PaymentEndpoint).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "Replay");
        var arguments = replay.GetParameters()
            .Select(parameter => parameter.ParameterType == typeof(HttpContext)
                ? (object)context
                : parameter.ParameterType == typeof(StoredOperation)
                    ? operation
                    : parameter.ParameterType == typeof(OperationGuardAspNetCoreOptions)
                        ? httpOptions
                        : parameter.ParameterType == typeof(OperationGuardOptions)
                            ? coreOptions
                            : throw new InvalidOperationException(
                                $"Unsupported PaymentApi replay dependency '{parameter.ParameterType}'."))
            .ToArray();
        var invocation = replay.Invoke(null, arguments);
        return invocation switch
        {
            IResult result => result,
            Task<IResult> pending => await pending,
            ValueTask<IResult> pending => await pending,
            _ => throw new InvalidOperationException("PaymentApi Replay must return an IResult."),
        };
    }

    private static ReplayResponse Response(
        int statusCode,
        IReadOnlyDictionary<string, string[]> headers) =>
        new(statusCode, headers, Encoding.UTF8.GetBytes("ok"));

    private static string Utf8Value(int byteCount)
    {
        const string multibyte = "€";
        var bytesPerCharacter = Encoding.UTF8.GetByteCount(multibyte);
        return string.Concat(Enumerable.Repeat(multibyte, byteCount / bytesPerCharacter))
            + new string('a', byteCount % bytesPerCharacter);
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

    private sealed record InvalidPaymentReplayCase(
        string Name,
        ContractOptions Options,
        ReplayResponse Response);
}
