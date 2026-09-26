namespace OperationGuard.Testing.Contracts.Models;

public enum ContractConditionalWriteKind
{
    Applied,
    StaleOwner,
    InvalidState,
    NotFound,
}
