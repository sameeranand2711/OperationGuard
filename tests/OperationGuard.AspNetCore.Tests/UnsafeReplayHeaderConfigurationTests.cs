using Microsoft.Extensions.DependencyInjection;
using OperationGuard.AspNetCore.Registration;
using OperationGuard.Core;
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
