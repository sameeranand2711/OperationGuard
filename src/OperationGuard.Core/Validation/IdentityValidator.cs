namespace OperationGuard.Core.Validation;

internal static class IdentityValidator
{
    public static void ValidateComponent(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, parameterName);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsHighSurrogate(current)
                && index + 1 < value.Length
                && char.IsLowSurrogate(value[index + 1]))
            {
                index++;
                continue;
            }

            if (char.IsSurrogate(current))
            {
                throw new ArgumentException(
                    "Operation identity components must contain well-formed UTF-16 text.",
                    parameterName);
            }
        }

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
