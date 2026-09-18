namespace OperationGuard.AspNetCore.Metadata;

public sealed record OperationGuardEndpointMetadata
{
    public OperationGuardEndpointMetadata(string operationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        OperationName = operationName;
    }

    public string OperationName { get; }
}
