using Microsoft.AspNetCore.Builder;

namespace OperationGuard.AspNetCore.Extensions;

public static class OperationGuardApplicationBuilderExtensions
{
    public static IApplicationBuilder UseOperationGuard(this IApplicationBuilder application)
    {
        ArgumentNullException.ThrowIfNull(application);
        return application.UseMiddleware<OperationGuardMiddleware>();
    }
}
