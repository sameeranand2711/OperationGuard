using OperationGuard.Core.Models;

namespace OperationGuard.Core.Internal;

internal static class ReplayPersistenceValidator
{
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
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

    public static void Validate(ReplayResponse? response, int replayBodyLimitBytes)
    {
        if (response is null)
        {
            return;
        }

        if (replayBodyLimitBytes is <= 0 or > OperationGuardOptions.HardReplayBodyLimitBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(replayBodyLimitBytes));
        }

        if (response.Body is { Length: var bodyLength } && bodyLength > replayBodyLimitBytes)
        {
            throw new ArgumentException(
                $"The replay body exceeds the configured limit of {replayBodyLimitBytes} bytes.",
                nameof(response));
        }

        ArgumentNullException.ThrowIfNull(response.Headers);
        foreach (var header in response.Headers)
        {
            if (!IsHttpToken(header.Key))
            {
                throw new ArgumentException("Replay response header names must be valid HTTP tokens.", nameof(response));
            }

            if (ForbiddenHeaders.Contains(header.Key))
            {
                throw new ArgumentException(
                    "Sensitive and hop-by-hop response headers cannot be persisted for replay.",
                    nameof(response));
            }

            ArgumentNullException.ThrowIfNull(header.Value);
            if (header.Value.Any(value => value is null || value.IndexOfAny(['\r', '\n', '\0']) >= 0))
            {
                throw new ArgumentException("Replay response header values contain invalid characters.", nameof(response));
            }
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
