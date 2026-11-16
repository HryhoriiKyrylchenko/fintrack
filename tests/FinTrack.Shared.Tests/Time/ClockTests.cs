using FinTrack.Shared.Kernel.Time;
using FinTrack.Tests.Common;

namespace FinTrack.Shared.Tests.Time;

public class ClockTests
{
    [Fact]
    public void SystemClock_ReturnsCurrentUtcTimeInWholeMilliseconds()
    {
        var clock = new SystemClock();

        // Repeated, because a single reading can land on a whole millisecond by chance.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var before = DateTimeOffset.UtcNow;
            var now = clock.UtcNow;
            var after = DateTimeOffset.UtcNow;

            now.Offset.ShouldBe(TimeSpan.Zero);
            (now.Ticks % TimeSpan.TicksPerMillisecond).ShouldBe(0);
            now.ShouldBeInRange(before.AddTicks(-(before.Ticks % TimeSpan.TicksPerMillisecond)), after);
        }
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

    [Fact]
    public void FakeClock_StartWithSubMillisecondTicks_Throws() =>
        Should.Throw<ArgumentException>(() => new FakeClock(FakeClock.DefaultStart.AddTicks(1)));

    [Fact]
    public void FakeClock_AdvanceBySubMillisecond_Throws() =>
        Should.Throw<ArgumentException>(() => new FakeClock().Advance(TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond + 1)));

    [Fact]
    public void FakeClock_SetWithSubMillisecondTicks_Throws() =>
        Should.Throw<ArgumentException>(() => new FakeClock().Set(FakeClock.DefaultStart.AddTicks(5)));
}
