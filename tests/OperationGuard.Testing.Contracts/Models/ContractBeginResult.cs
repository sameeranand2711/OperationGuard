namespace OperationGuard.Testing.Contracts.Models;

public sealed record ContractBeginResult(
    ContractBeginKind Kind,
    string? OwnerToken,
    ContractStoredOperation? Operation);
