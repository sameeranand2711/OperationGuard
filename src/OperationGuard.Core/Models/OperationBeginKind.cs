namespace OperationGuard.Core.Models;

public enum OperationBeginKind
{
    Acquired,
    AlreadyInProgress,
    Completed,
    Indeterminate,
    FingerprintMismatch,
}
