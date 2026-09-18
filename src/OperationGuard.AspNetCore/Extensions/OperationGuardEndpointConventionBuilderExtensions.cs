using Microsoft.AspNetCore.Builder;
using OperationGuard.AspNetCore.Metadata;

namespace OperationGuard.AspNetCore.Extensions;

public static class OperationGuardEndpointConventionBuilderExtensions
{
    public static TBuilder RequireOperationGuard<TBuilder>(this TBuilder builder, string operationName)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(endpointBuilder => endpointBuilder.Metadata.Add(new OperationGuardEndpointMetadata(operationName)));
        return builder;
    }
}
