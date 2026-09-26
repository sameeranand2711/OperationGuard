namespace OperationGuard.Testing.Contracts.Models;

public enum ContractBeginKind
{
    Acquired,
    AlreadyInProgress,
    Completed,
    Indeterminate,
    FingerprintMismatch,
}
