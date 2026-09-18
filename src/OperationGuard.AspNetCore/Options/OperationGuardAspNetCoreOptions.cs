using Microsoft.AspNetCore.Http;
using OperationGuard.Core;

namespace OperationGuard.AspNetCore;

public sealed class OperationGuardAspNetCoreOptions
{
    private static readonly HashSet<string> ForbiddenReplayHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Connection",
        "Cookie",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "Set-Cookie",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
    };

    public string IdempotencyKeyHeaderName { get; set; } = "Idempotency-Key";

    public int MaximumKeyLength { get; set; } = OperationGuardOptions.DefaultMaximumKeyLength;

    public int FingerprintBodyLimitBytes { get; set; } = OperationGuardOptions.DefaultFingerprintBodyLimitBytes;

    public int ReplayBodyLimitBytes { get; set; } = OperationGuardOptions.DefaultReplayBodyLimitBytes;

    public TimeSpan CompletedRetention { get; set; } = TimeSpan.FromHours(24);

    public TimeSpan InProgressStaleAfter { get; set; } = TimeSpan.FromMinutes(15);

    public IReadOnlyCollection<string> FingerprintHeaders { get; set; } = Array.Empty<string>();

    public IReadOnlyCollection<string> ReplayHeaders { get; set; } = ["Content-Type", "ETag", "Location"];

    public Func<HttpContext, CancellationToken, ValueTask<string>> ScopeResolver { get; set; } =
        static (_, _) => ValueTask.FromResult("global");

    internal OperationGuardOptions ToCoreOptions()
    {
        var options = new OperationGuardOptions
        {
            MaximumKeyLength = MaximumKeyLength,
            FingerprintBodyLimitBytes = FingerprintBodyLimitBytes,
            ReplayBodyLimitBytes = ReplayBodyLimitBytes,
            CompletedRetention = CompletedRetention,
            InProgressStaleAfter = InProgressStaleAfter,
        };
        options.Validate();
        if (string.IsNullOrWhiteSpace(IdempotencyKeyHeaderName))
        {
            throw new ArgumentException("The idempotency key header name is required.", nameof(IdempotencyKeyHeaderName));
        }

        ArgumentNullException.ThrowIfNull(ScopeResolver);
        ValidateHeaderNames(FingerprintHeaders, nameof(FingerprintHeaders));
        ValidateHeaderNames(ReplayHeaders, nameof(ReplayHeaders));
        if (ReplayHeaders.Any(ForbiddenReplayHeaders.Contains))
        {
            throw new ArgumentException(
                "Sensitive and hop-by-hop response headers cannot be persisted for replay.",
                nameof(ReplayHeaders));
        }

        return options;
    }

    private static void ValidateHeaderNames(IReadOnlyCollection<string> names, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(names, parameterName);
        if (names.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Header names cannot be empty.", parameterName);
        }
    }
}
