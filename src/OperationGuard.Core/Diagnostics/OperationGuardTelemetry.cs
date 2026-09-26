using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OperationGuard.Core.Diagnostics;

public static class OperationGuardTelemetry
{
    public const string InstrumentationName = "OperationGuard";

    public static ActivitySource ActivitySource { get; } = new(InstrumentationName);

    public static Meter Meter { get; } = new(InstrumentationName);
}
