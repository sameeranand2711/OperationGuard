namespace OperationGuard.Testing.Contracts.Models;

public sealed record ContractOptions(
    int MaximumKeyLength = 255,
    int FingerprintBodyLimitBytes = 1024 * 1024,
    int ReplayBodyLimitBytes = 64 * 1024,
    int MaximumReplayHeaderCount = 32,
    int MaximumReplayHeaderValueBytes = 8 * 1024,
    int MaximumReplayHeadersTotalBytes = 32 * 1024,
    TimeSpan? CompletedRetention = null,
    TimeSpan? InProgressStaleAfter = null)
{
    public TimeSpan EffectiveCompletedRetention => CompletedRetention ?? TimeSpan.FromHours(24);

    public TimeSpan EffectiveInProgressStaleAfter => InProgressStaleAfter ?? TimeSpan.FromMinutes(15);
}
