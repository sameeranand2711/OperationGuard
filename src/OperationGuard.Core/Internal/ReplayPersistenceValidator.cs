using System.Text;
using System.Text.Json;
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

    public static void ValidateForPersistence(
        ReplayResponse? response,
        OperationGuardOptions options) =>
        Validate(response, options, rejectForbiddenHeaders: true);

    public static void ValidateForReplay(
        ReplayResponse response,
        OperationGuardOptions options) =>
        Validate(response, options, rejectForbiddenHeaders: false);

    public static bool IsHeaderAllowed(
        string name,
        IReadOnlyCollection<string> allowlist) =>
        IsHttpToken(name)
        && !ForbiddenHeaders.Contains(name)
        && allowlist.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static void Validate(
        ReplayResponse? response,
        OperationGuardOptions options,
        bool rejectForbiddenHeaders)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (response is null)
        {
            return;
        }

        if (response.StatusCode is < 200 or > 599)
        {
            throw new ArgumentException(
                "Replay responses must use a final HTTP status code between 200 and 599.",
                nameof(response));
        }

        if (response.Body is { Length: var bodyLength } && bodyLength > options.ReplayBodyLimitBytes)
        {
            throw new ArgumentException(
                $"The replay body exceeds the configured limit of {options.ReplayBodyLimitBytes} bytes.",
                nameof(response));
        }

        ArgumentNullException.ThrowIfNull(response.Headers);
        if (response.Headers.Count > options.MaximumReplayHeaderCount)
        {
            throw new ArgumentException(
                $"The replay response exceeds the configured limit of {options.MaximumReplayHeaderCount} headers.",
                nameof(response));
        }

        foreach (var header in response.Headers)
        {
            if (!IsHttpToken(header.Key))
            {
                throw new ArgumentException("Replay response header names must be valid HTTP tokens.", nameof(response));
            }

            if (rejectForbiddenHeaders && ForbiddenHeaders.Contains(header.Key))
            {
                throw new ArgumentException(
                    "Sensitive and hop-by-hop response headers cannot be persisted for replay.",
                    nameof(response));
            }

            if (header.Value is null)
            {
                throw new ArgumentException("Replay response header value arrays cannot be null.", nameof(response));
            }

            foreach (var value in header.Value)
            {
                if (value is null || value.Any(IsInvalidHeaderValueCharacter))
                {
                    throw new ArgumentException("Replay response header values contain invalid characters.", nameof(response));
                }

                if (Encoding.UTF8.GetByteCount(value) > options.MaximumReplayHeaderValueBytes)
                {
                    throw new ArgumentException(
                        $"A replay response header value exceeds the configured limit of {options.MaximumReplayHeaderValueBytes} UTF-8 bytes.",
                        nameof(response));
                }
            }
        }

        var serializedHeaders = JsonSerializer.SerializeToUtf8Bytes(response.Headers);
        if (serializedHeaders.Length > options.MaximumReplayHeadersTotalBytes)
        {
            throw new ArgumentException(
                $"The serialized replay response headers exceed the configured limit of {options.MaximumReplayHeadersTotalBytes} UTF-8 bytes.",
                nameof(response));
        }
    }

    private static bool IsInvalidHeaderValueCharacter(char value) =>
        value is < ' ' and not '\t' or '\u007f';

    private static bool IsHttpToken(string value) =>
        !string.IsNullOrEmpty(value) && value.All(IsTokenCharacter);

    private static bool IsTokenCharacter(char value) =>
        value is >= '0' and <= '9'
            or >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.'
            or '^' or '_' or '`' or '|' or '~';
}
