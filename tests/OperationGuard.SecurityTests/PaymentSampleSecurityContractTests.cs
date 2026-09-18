using System.Reflection;
using Microsoft.AspNetCore.Http;
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
}
