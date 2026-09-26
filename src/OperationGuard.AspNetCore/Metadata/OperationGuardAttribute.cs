namespace OperationGuard.AspNetCore.Metadata;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class OperationGuardAttribute : Attribute
{
    public OperationGuardAttribute(string operationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        OperationName = operationName;
    }

    public string OperationName { get; }
}
