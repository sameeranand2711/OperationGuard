using System.Reflection;
using OperationGuard.Testing.Contracts.Models;

namespace OperationGuard.Testing.Contracts.Drivers;

public static class ContractOptionsBinding
{
    public static void ApplyReplayHeaderLimits(object target, ContractOptions options)
    {
        ArgumentNullException.ThrowIfNull(target);
        SetRequiredProperty(target, nameof(options.MaximumReplayHeaderCount), options.MaximumReplayHeaderCount);
        SetRequiredProperty(target, nameof(options.MaximumReplayHeaderValueBytes), options.MaximumReplayHeaderValueBytes);
        SetRequiredProperty(target, nameof(options.MaximumReplayHeadersTotalBytes), options.MaximumReplayHeadersTotalBytes);
    }

    public static bool TryApplyReplayHeaderLimits(object target, ContractOptions options)
    {
        ArgumentNullException.ThrowIfNull(target);
        var propertyNames = new[]
        {
            nameof(options.MaximumReplayHeaderCount),
            nameof(options.MaximumReplayHeaderValueBytes),
            nameof(options.MaximumReplayHeadersTotalBytes),
        };
        var properties = propertyNames
            .Select(name => target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public))
            .ToArray();
        if (properties.Any(static property => property is null))
        {
            return false;
        }

        properties[0]!.SetValue(target, options.MaximumReplayHeaderCount);
        properties[1]!.SetValue(target, options.MaximumReplayHeaderValueBytes);
        properties[2]!.SetValue(target, options.MaximumReplayHeadersTotalBytes);
        return true;
    }

    public static void SetRequiredProperty(object target, string propertyName, object value)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(target.GetType().FullName, propertyName);
        property.SetValue(target, value);
    }
}
