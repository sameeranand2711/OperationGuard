namespace OperationGuard.Testing.Contracts.Models;

public sealed record ContractHandlerResponse(
    int StatusCode,
    IReadOnlyDictionary<string, string[]> Headers,
    byte[] Body,
    bool ThrowAfterPossibleSideEffect = false);
