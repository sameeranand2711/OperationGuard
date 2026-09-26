using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OperationGuard.Core.Abstractions;
using OperationGuard.Core.Fingerprinting;

namespace OperationGuard.AspNetCore.Registration;

public static class OperationGuardServiceCollectionExtensions
{
    public static IServiceCollection AddOperationGuard(
        this IServiceCollection services,
        Action<OperationGuardAspNetCoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new OperationGuardAspNetCoreOptions();
        configure?.Invoke(options);
        var coreOptions = options.ToCoreOptions();

        services.AddSingleton(options);
        services.AddSingleton(coreOptions);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRequestFingerprintProvider>(
            new Sha256RequestFingerprintProvider(options.FingerprintBodyLimitBytes));
        return services;
    }
}
