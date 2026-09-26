namespace OperationGuard.Testing.Contracts.Models;

public sealed record ContractHttpRequest(
    string Scope,
    string OperationName,
    string Method,
    string Query,
    string? IdempotencyKey,
    string ContentType,
    byte[] Body,
    IReadOnlyDictionary<string, string[]>? Headers = null,
    bool UseCustomFingerprint = false);
