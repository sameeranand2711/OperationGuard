namespace OperationGuard.Testing.Contracts.Models;

public sealed record ContractIdentity(string Scope, string OperationName, string IdempotencyKey);
