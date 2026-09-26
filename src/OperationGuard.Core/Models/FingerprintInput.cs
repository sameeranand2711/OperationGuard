namespace OperationGuard.Core.Models;

public sealed record FingerprintInput(
    string OperationName,
    string Method,
    string Query,
    string ContentType,
    ReadOnlyMemory<byte> Body,
    IReadOnlyDictionary<string, string[]>? SelectedHeaders = null);
