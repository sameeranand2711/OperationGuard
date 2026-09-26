using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using OperationGuard.Core.Abstractions;
using OperationGuard.Core.Models;

namespace OperationGuard.Core.Fingerprinting;

public sealed class Sha256RequestFingerprintProvider : IRequestFingerprintProvider
{
    private readonly int _bodyLimitBytes;

    public Sha256RequestFingerprintProvider(int bodyLimitBytes = OperationGuardOptions.DefaultFingerprintBodyLimitBytes)
    {
        if (bodyLimitBytes is <= 0 or > OperationGuardOptions.HardFingerprintBodyLimitBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(bodyLimitBytes));
        }

        _bodyLimitBytes = bodyLimitBytes;
    }

    public ValueTask<OperationFingerprint> CreateAsync(
        FingerprintInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Body.Length > _bodyLimitBytes)
        {
            throw new ArgumentException($"The request body exceeds the configured fingerprint limit of {_bodyLimitBytes} bytes.", nameof(input));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "operationguard-http-fingerprint-v1");
        Append(hash, input.OperationName);
        Append(hash, input.Method.ToUpperInvariant());
        Append(hash, CanonicalizeQuery(input.Query));
        Append(hash, input.ContentType);

        var selectedHeaders = (input.SelectedHeaders ?? new Dictionary<string, string[]>())
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .ToArray();

        Append(hash, selectedHeaders.Length);
        foreach (var header in selectedHeaders)
        {
            Append(hash, header.Key.ToUpperInvariant());
            Append(hash, header.Value.Length);
            foreach (var value in header.Value)
            {
                Append(hash, value);
            }
        }

        Append(hash, input.Body.Span);
        return ValueTask.FromResult(OperationFingerprint.Sha256(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));
    }

    private static string CanonicalizeQuery(string query)
    {
        var value = query.StartsWith('?') ? query[1..] : query;
        return string.Join(
            '&',
            value.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select((part, index) => new QueryPart(
                    part,
                    part.AsSpan().IndexOf('=') is var equalsIndex && equalsIndex >= 0
                        ? part[..equalsIndex]
                        : part,
                    index))
                .GroupBy(part => part.Key, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .SelectMany(group => group.OrderBy(part => part.Index))
                .Select(part => part.Value));
    }

    private static void Append(IncrementalHash hash, string value) => Append(hash, Encoding.UTF8.GetBytes(value));

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Append(hash, value.Length);
        hash.AppendData(value);
    }

    private static void Append(IncrementalHash hash, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        hash.AppendData(buffer);
    }

    private readonly record struct QueryPart(string Value, string Key, int Index);
}
