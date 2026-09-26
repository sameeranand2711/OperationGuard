namespace OperationGuard.Core.Models;

public sealed record OperationBeginResult(
    OperationBeginKind Kind,
    string? OwnerToken,
    StoredOperation? Operation);
