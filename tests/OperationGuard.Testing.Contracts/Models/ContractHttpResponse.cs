namespace OperationGuard.Testing.Contracts.Models;

public sealed record ContractHttpResponse(
    int StatusCode,
    IReadOnlyDictionary<string, string[]> Headers,
    byte[] Body,
    string? ProblemType = null);
