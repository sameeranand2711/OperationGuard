namespace OperationGuard.Core.Internal;

internal sealed record RelationalStoreSql(
    string Insert,
    string Select,
    string ReservationSelect,
    string Complete,
    string MarkIndeterminate,
    string ResolveIndeterminate,
    string AuthorizeRecovery,
    string DeleteExpired);
