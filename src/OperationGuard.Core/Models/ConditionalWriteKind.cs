namespace OperationGuard.Core.Models;

public enum ConditionalWriteKind
{
    Applied,
    StaleOwner,
    InvalidState,
    NotFound,
}
