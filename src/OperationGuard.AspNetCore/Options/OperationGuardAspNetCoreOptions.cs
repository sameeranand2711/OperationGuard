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
        ValidateHeaderName(IdempotencyKeyHeaderName, nameof(IdempotencyKeyHeaderName));

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
        foreach (var name in names)
        {
            ValidateHeaderName(name, parameterName);
        }
    }

    internal bool IsReplayHeaderAllowed(string name) =>
        IsHttpToken(name)
        && !ForbiddenReplayHeaders.Contains(name)
        && ReplayHeaders.Contains(name, StringComparer.OrdinalIgnoreCase);

    internal static bool HasSafeHeaderValues(IEnumerable<string?> values) =>
        values.All(value => value is not null && value.IndexOfAny(['\r', '\n', '\0']) < 0);

    private static void ValidateHeaderName(string name, string parameterName)
    {
        if (!IsHttpToken(name))
        {
            throw new ArgumentException("Header names must be valid RFC HTTP tokens.", parameterName);
        }
    }

    private static bool IsHttpToken(string value) =>
        !string.IsNullOrEmpty(value) && value.All(IsTokenCharacter);

    private static bool IsTokenCharacter(char value) =>
        value is >= '0' and <= '9'
            or >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.'
            or '^' or '_' or '`' or '|' or '~';
}
