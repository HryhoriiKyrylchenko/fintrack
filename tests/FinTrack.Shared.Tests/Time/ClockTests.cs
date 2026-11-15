using FinTrack.Shared.Kernel.Time;
using FinTrack.Tests.Common;

namespace FinTrack.Shared.Tests.Time;

public class ClockTests
{
    [Fact]
    public void SystemClock_ReturnsCurrentUtcTime()
    {
        var before = DateTimeOffset.UtcNow;

        var now = new SystemClock().UtcNow;

        var after = DateTimeOffset.UtcNow;
        now.Offset.ShouldBe(TimeSpan.Zero);
        now.ShouldBeInRange(before, after);
    }

    [Fact]
    public void FakeClock_StartsAtFixedInstant() =>
        new FakeClock().UtcNow.ShouldBe(FakeClock.DefaultStart);

    [Fact]
    public void FakeClock_Advance_MovesTimeForward()
    {
        var clock = new FakeClock();

        clock.Advance(TimeSpan.FromMinutes(5));

        clock.UtcNow.ShouldBe(FakeClock.DefaultStart.AddMinutes(5));
    }

    [Fact]
    public void FakeClock_AdvanceByNegative_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new FakeClock().Advance(TimeSpan.FromSeconds(-1)));

    [Fact]
    public void FakeClock_NonUtcStart_Throws() =>
        Should.Throw<ArgumentException>(() => new FakeClock(new DateTimeOffset(2026, 1, 15, 9, 30, 0, TimeSpan.FromHours(1))));
}
