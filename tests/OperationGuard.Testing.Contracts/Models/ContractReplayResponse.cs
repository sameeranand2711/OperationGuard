namespace OperationGuard.Testing.Contracts.Models;

public sealed record ContractReplayResponse(
    int StatusCode,
    IReadOnlyDictionary<string, string[]> Headers,
    byte[]? Body);
