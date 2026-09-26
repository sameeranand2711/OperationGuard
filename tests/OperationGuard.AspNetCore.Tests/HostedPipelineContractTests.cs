using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OperationGuard.AspNetCore.Extensions;
using OperationGuard.AspNetCore.Metadata;
using OperationGuard.AspNetCore.Registration;
using OperationGuard.Core.Abstractions;
using OperationGuard.Core.Models;
using OperationGuard.Core.Testing;
using Xunit;

namespace OperationGuard.AspNetCore.Tests;

public sealed class HostedPipelineContractTests
{
    [Fact]
    public async Task Hosted_minimal_endpoint_uses_routing_metadata_and_authentication_before_reservation()
    {
        var invocations = new InvocationCounter();
        await using var application = await StartAsync(
            services => ConfigureServices(services, invocations),
            app =>
            {
                app.UseRouting();
                UseTestAuthentication(app);
                app.UseOperationGuard();
                app.UseEndpoints(endpoints => endpoints
                    .MapPost("/minimal", async context =>
                    {
                        invocations.Value++;
                        context.Response.StatusCode = StatusCodes.Status201Created;
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync("created");
                    })
                    .RequireOperationGuard("Hosted.Minimal"));
            });

        var unauthorized = await application.SendAsync("/minimal", "unauthorized-key", subject: null);
        var first = await application.SendAsync("/minimal?item=a&item=b", "same-key", "tenant-a");
        var replay = await application.SendAsync("/minimal?item=a&item=b", "same-key", "tenant-a");
        var mismatch = await application.SendAsync("/minimal?item=b&item=a", "same-key", "tenant-a");
        var store = application.Services.GetRequiredService<InMemoryOperationStore>();

        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatch.StatusCode);
        Assert.Equal(1, invocations.Value);
        Assert.Null(await store.ReadOutcomeAsync(
            new OperationIdentity("tenant-a", "Hosted.Minimal", "unauthorized-key")));
    }

    [Fact]
    public async Task Hosted_mvc_attribute_uses_the_same_guard_pipeline_and_replays_without_reexecution()
    {
        var invocations = new InvocationCounter();
        await using var application = await StartAsync(
            services =>
            {
                ConfigureServices(services, invocations);
                services.AddControllers().AddApplicationPart(typeof(HostedGuardController).Assembly);
            },
            app =>
            {
                app.UseRouting();
                UseTestAuthentication(app);
                app.UseOperationGuard();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            });

        var first = await application.SendAsync("/hosted-mvc", "mvc-key", "tenant-a");
        var replay = await application.SendAsync("/hosted-mvc", "mvc-key", "tenant-a");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal("accepted", await replay.Content.ReadAsStringAsync());
        Assert.Equal(1, invocations.Value);
    }

    [Fact]
    public async Task Middleware_before_routing_has_no_endpoint_metadata_and_is_an_unsupported_order()
    {
        var invocations = new InvocationCounter();
        await using var application = await StartAsync(
            services => ConfigureServices(services, invocations),
            app =>
            {
                app.UseOperationGuard();
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints
                    .MapPost("/misordered", context =>
                    {
                        invocations.Value++;
                        return context.Response.WriteAsync("unprotected because routing ran too late");
                    })
                    .RequireOperationGuard("Hosted.Misordered"));
            });

        await application.SendAsync("/misordered", "same-key", subject: null);
        await application.SendAsync("/misordered", "same-key", subject: null);

        Assert.Equal(2, invocations.Value);
    }

    [Fact]
    public async Task Response_started_before_guard_never_allows_a_missing_key_request_to_reach_the_handler()
    {
        var invocations = new InvocationCounter();
        await using var application = await StartAsync(
            services => ConfigureServices(services, invocations),
            app =>
            {
                app.UseRouting();
                app.Use(async (context, next) =>
                {
                    await context.Response.StartAsync();
                    await next();
                });
                app.UseOperationGuard();
                app.UseEndpoints(endpoints => endpoints
                    .MapPost("/started", context =>
                    {
                        invocations.Value++;
                        return context.Response.WriteAsync("must not execute");
                    })
                    .RequireOperationGuard("Hosted.Started"));
            });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/started")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        _ = await Record.ExceptionAsync(async () => await application.Client.SendAsync(request));

        Assert.Equal(0, invocations.Value);
    }

    private static void ConfigureServices(IServiceCollection services, InvocationCounter invocations)
    {
        services.AddRouting();
        services.AddLogging();
        services.AddSingleton(invocations);
        services.AddSingleton<InMemoryOperationStore>();
        services.AddSingleton<IOperationStore>(provider => provider.GetRequiredService<InMemoryOperationStore>());
        services.AddOperationGuard(options => options.ScopeResolver = static (context, _) =>
            ValueTask.FromResult(
                context.User.Identity?.Name
                ?? throw new InvalidOperationException("Authentication must run before OperationGuard.")));
    }

    private static void UseTestAuthentication(IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!context.Request.Headers.TryGetValue("X-Test-Subject", out var subject)
                || subject.Count != 1
                || string.IsNullOrWhiteSpace(subject[0]))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, subject[0]!)],
                authenticationType: "test"));
            await next();
        });

    private static async Task<HostedApplication> StartAsync(
        Action<IServiceCollection> configureServices,
        Action<IApplicationBuilder> configure)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseKestrel()
                .UseUrls("http://127.0.0.1:0")
                .ConfigureServices(configureServices)
                .Configure(configure))
            .Build();
        await host.StartAsync();
        var addresses = host.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses;
        var address = Assert.Single(addresses!);
        return new HostedApplication(host, new HttpClient { BaseAddress = new Uri(address) });
    }

    private sealed class HostedApplication(IHost host, HttpClient client) : IAsyncDisposable
    {
        public IServiceProvider Services => host.Services;

        public HttpClient Client { get; } = client;

        public async Task<HttpResponseMessage> SendAsync(string path, string key, string? subject)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Idempotency-Key", key);
            if (subject is not null)
            {
                request.Headers.Add("X-Test-Subject", subject);
            }

            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await host.StopAsync();
            host.Dispose();
        }
    }
}
