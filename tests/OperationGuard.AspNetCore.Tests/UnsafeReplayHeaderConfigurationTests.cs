using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OperationGuard.AspNetCore.Metadata;
using OperationGuard.AspNetCore.Registration;
using OperationGuard.Core;
using OperationGuard.Core.Fingerprinting;
using OperationGuard.Core.Models;
using OperationGuard.Core.Testing;
using OperationGuard.Testing.Contracts.Drivers;
using OperationGuard.Testing.Contracts.Models;
using Xunit;

namespace OperationGuard.AspNetCore.Tests;

public sealed class UnsafeReplayHeaderConfigurationTests
{
    [Fact]
    public void Replay_header_limits_have_frozen_defaults_in_http_and_core_options()
    {
        var services = new ServiceCollection();
        services.AddOperationGuard();

        using var provider = services.BuildServiceProvider();
        foreach (var options in new object[]
                 {
                     provider.GetRequiredService<OperationGuardAspNetCoreOptions>(),
                     provider.GetRequiredService<OperationGuardOptions>(),
                 })
        {
            Assert.Equal(32, ReadInt32(options, "MaximumReplayHeaderCount"));
            Assert.Equal(8 * 1024, ReadInt32(options, "MaximumReplayHeaderValueBytes"));
            Assert.Equal(32 * 1024, ReadInt32(options, "MaximumReplayHeadersTotalBytes"));
        }
    }

    [Theory]
    [InlineData("Set-Cookie")]
    [InlineData("Authorization")]
    [InlineData("Connection")]
    [InlineData("Transfer-Encoding")]
    public void Sensitive_and_hop_by_hop_headers_cannot_be_enabled(string headerName)
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddOperationGuard(options =>
            options.ReplayHeaders = [headerName]));
    }

    [Theory]
    [InlineData("Bad Header")]
    [InlineData("Bad:Header")]
    [InlineData("Bad(Header)")]
    [InlineData("Bad/Header")]
    [InlineData("Bad\tHeader")]
    [InlineData("Bad\r\nInjected")]
    public void Invalid_http_header_name_tokens_are_rejected(string headerName)
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddOperationGuard(options =>
            options.IdempotencyKeyHeaderName = headerName));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddOperationGuard(options =>
            options.FingerprintHeaders = [headerName]));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddOperationGuard(options =>
            options.ReplayHeaders = [headerName]));
    }

    [Fact]
    public void Header_configuration_rejects_case_insensitive_duplicates()
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddOperationGuard(options =>
            options.FingerprintHeaders = ["X-Business-Version", "x-business-version"]));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddOperationGuard(options =>
            options.ReplayHeaders = ["ETag", "etag"]));
    }

    [Fact]
    public async Task Replay_response_construction_failure_after_handler_marks_operation_indeterminate()
    {
        var identity = new OperationIdentity("tenant", "Payments.Create", "construction-failure");
        var options = new OperationGuardAspNetCoreOptions
        {
            ReplayHeaders = ["ETag", "etag"],
            ScopeResolver = static (_, _) => ValueTask.FromResult("tenant"),
        };
        var coreOptions = new OperationGuardOptions();
        var store = new InMemoryOperationStore(coreOptions);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        context.Request.ContentLength = context.Request.Body.Length;
        context.Request.Headers["Idempotency-Key"] = identity.IdempotencyKey;
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(new Endpoint(
            null,
            new EndpointMetadataCollection(new OperationGuardEndpointMetadata(identity.OperationName)),
            identity.OperationName));
        var middleware = new OperationGuardMiddleware(
            httpContext =>
            {
                httpContext.Response.Headers.ETag = "safe";
                return Task.CompletedTask;
            },
            options,
            coreOptions,
            TimeProvider.System,
            NullLogger<OperationGuardMiddleware>.Instance);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => middleware.InvokeAsync(
            context,
            store,
            new Sha256RequestFingerprintProvider()));

        var stored = await store.ReadOutcomeAsync(identity);
        Assert.Equal(OperationState.Indeterminate, stored?.State);
    }

    [Fact]
    public void Replay_header_limits_are_copied_to_core_options()
    {
        var limits = new ContractOptions(
            MaximumReplayHeaderCount: 17,
            MaximumReplayHeaderValueBytes: 2048,
            MaximumReplayHeadersTotalBytes: 8192);
        var services = new ServiceCollection();
        services.AddOperationGuard(options => ContractOptionsBinding.ApplyReplayHeaderLimits(options, limits));

        using var provider = services.BuildServiceProvider();
        var aspOptions = provider.GetRequiredService<OperationGuardAspNetCoreOptions>();
        var coreOptions = provider.GetRequiredService<OperationGuardOptions>();

        AssertLimitProperties(limits, aspOptions);
        AssertLimitProperties(limits, coreOptions);
    }

    [Fact]
    public void Replay_header_limit_relationship_is_validated_during_registration()
    {
        var limits = new ContractOptions(
            MaximumReplayHeaderValueBytes: 1024,
            MaximumReplayHeadersTotalBytes: 1023);

        Assert.ThrowsAny<ArgumentException>(() => new ServiceCollection().AddOperationGuard(
            options => ContractOptionsBinding.ApplyReplayHeaderLimits(options, limits)));
    }

    [Theory]
    [InlineData("MaximumReplayHeaderCount", 0)]
    [InlineData("MaximumReplayHeaderCount", 129)]
    [InlineData("MaximumReplayHeaderValueBytes", 0)]
    [InlineData("MaximumReplayHeaderValueBytes", 65537)]
    [InlineData("MaximumReplayHeadersTotalBytes", 0)]
    [InlineData("MaximumReplayHeadersTotalBytes", 262145)]
    public void Replay_header_limits_reject_non_positive_and_above_hard_ceiling_values(
        string propertyName,
        int value)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ServiceCollection().AddOperationGuard(
            options => ContractOptionsBinding.SetRequiredProperty(options, propertyName, value)));
    }

    private static void AssertLimitProperties(ContractOptions expected, object actual)
    {
        Assert.Equal(expected.MaximumReplayHeaderCount, ReadInt32(actual, nameof(expected.MaximumReplayHeaderCount)));
        Assert.Equal(expected.MaximumReplayHeaderValueBytes, ReadInt32(actual, nameof(expected.MaximumReplayHeaderValueBytes)));
        Assert.Equal(expected.MaximumReplayHeadersTotalBytes, ReadInt32(actual, nameof(expected.MaximumReplayHeadersTotalBytes)));
    }

    private static int ReadInt32(object source, string propertyName) =>
        (int)(source.GetType().GetProperty(propertyName)?.GetValue(source)
            ?? throw new MissingMemberException(source.GetType().FullName, propertyName));
}
