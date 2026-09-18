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
}
