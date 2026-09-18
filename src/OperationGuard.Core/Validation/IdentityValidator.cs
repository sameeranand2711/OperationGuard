namespace OperationGuard.Core.Validation;

internal static class IdentityValidator
{
    public static void ValidateComponent(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, parameterName);
        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("Operation identity components cannot contain control characters.", parameterName);
        }
    }

    public static void ValidateKey(string value, int maximumLength)
    {
        ValidateComponent(value, nameof(value));
        if (maximumLength is <= 0 or > OperationGuardOptions.HardMaximumKeyLength)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        }

        if (value.Length > maximumLength)
        {
            throw new ArgumentException($"The idempotency key exceeds the configured limit of {maximumLength} characters.", nameof(value));
        }
    }
}
