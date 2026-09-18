namespace OperationGuard.Core;

public sealed class OperationGuardOptions
{
    public const int DefaultMaximumKeyLength = 255;
    public const int HardMaximumKeyLength = 1024;
    public const int DefaultFingerprintBodyLimitBytes = 1024 * 1024;
    public const int HardFingerprintBodyLimitBytes = 16 * 1024 * 1024;
    public const int DefaultReplayBodyLimitBytes = 64 * 1024;
    public const int HardReplayBodyLimitBytes = 1024 * 1024;

    public int MaximumKeyLength { get; set; } = DefaultMaximumKeyLength;

    public int FingerprintBodyLimitBytes { get; set; } = DefaultFingerprintBodyLimitBytes;

    public int ReplayBodyLimitBytes { get; set; } = DefaultReplayBodyLimitBytes;

    public TimeSpan CompletedRetention { get; set; } = TimeSpan.FromHours(24);

    public TimeSpan InProgressStaleAfter { get; set; } = TimeSpan.FromMinutes(15);

    public void Validate()
    {
        if (MaximumKeyLength is <= 0 or > HardMaximumKeyLength)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumKeyLength));
        }

        if (FingerprintBodyLimitBytes is <= 0 or > HardFingerprintBodyLimitBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(FingerprintBodyLimitBytes));
        }

        if (ReplayBodyLimitBytes is <= 0 or > HardReplayBodyLimitBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(ReplayBodyLimitBytes));
        }

        if (CompletedRetention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CompletedRetention));
        }

        if (InProgressStaleAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(InProgressStaleAfter));
        }
    }
}
