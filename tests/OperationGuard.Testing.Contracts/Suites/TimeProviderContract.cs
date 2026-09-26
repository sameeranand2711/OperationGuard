using OperationGuard.Testing.Contracts.Time;
using Xunit;

namespace OperationGuard.Testing.Contracts.Suites;

public sealed class TimeProviderContract
{
    [Fact]
    public void Time_advances_only_when_test_requests_it()
    {
        var initial = new DateTimeOffset(2035, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var time = new ManualTimeProvider(initial);

        Assert.Equal(initial, time.GetUtcNow());
        time.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(initial.AddMinutes(15), time.GetUtcNow());
    }

    [Fact]
    public void Time_cannot_move_backwards()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);

        Assert.Throws<ArgumentOutOfRangeException>(() => time.Advance(TimeSpan.FromTicks(-1)));
    }
}
