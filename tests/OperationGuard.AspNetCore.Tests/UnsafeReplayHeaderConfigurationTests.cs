using Microsoft.Extensions.DependencyInjection;
using OperationGuard.AspNetCore.Registration;
using Xunit;

namespace OperationGuard.AspNetCore.Tests;

public sealed class UnsafeReplayHeaderConfigurationTests
{
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
}
