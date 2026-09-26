using OperationGuard.Core.Validation;

namespace OperationGuard.Core.Models;

public sealed record OperationIdentity
{
    public OperationIdentity(string scope, string operationName, string idempotencyKey)
        : this(scope, operationName, idempotencyKey, OperationGuardOptions.DefaultMaximumKeyLength)
    {
    }

    private OperationIdentity(string scope, string operationName, string idempotencyKey, int maximumKeyLength)
    {
        IdentityValidator.ValidateComponent(scope, nameof(scope));
        IdentityValidator.ValidateComponent(operationName, nameof(operationName));
        IdentityValidator.ValidateKey(idempotencyKey, maximumKeyLength);
        Scope = scope;
        OperationName = operationName;
        IdempotencyKey = idempotencyKey;
    }

    public string Scope { get; }

    public string OperationName { get; }

    public string IdempotencyKey { get; }

    public static OperationIdentity Create(
        string scope,
        string operationName,
        string idempotencyKey,
        int maximumKeyLength) => new(scope, operationName, idempotencyKey, maximumKeyLength);
}
