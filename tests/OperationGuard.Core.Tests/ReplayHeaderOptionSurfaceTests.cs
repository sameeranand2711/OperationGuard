using OperationGuard.Core;
using Xunit;

namespace OperationGuard.Core.Tests;

public sealed class ReplayHeaderOptionSurfaceTests
{
    [Fact]
    public void Core_replay_header_limits_have_frozen_conservative_defaults()
    {
        var options = new OperationGuardOptions();

        Assert.Equal(32, ReadInt32(options, "MaximumReplayHeaderCount"));
        Assert.Equal(8 * 1024, ReadInt32(options, "MaximumReplayHeaderValueBytes"));
        Assert.Equal(32 * 1024, ReadInt32(options, "MaximumReplayHeadersTotalBytes"));
    }

    private static int ReadInt32(object source, string propertyName) =>
        (int)(source.GetType().GetProperty(propertyName)?.GetValue(source)
            ?? throw new MissingMemberException(source.GetType().FullName, propertyName));
}
