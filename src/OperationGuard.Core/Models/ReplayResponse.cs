namespace OperationGuard.Core.Models;

public sealed record ReplayResponse(
    int StatusCode,
    IReadOnlyDictionary<string, string[]> Headers,
    byte[]? Body);
