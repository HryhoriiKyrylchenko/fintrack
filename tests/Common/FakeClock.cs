using FinTrack.Shared.Kernel.Time;

namespace FinTrack.Tests.Common;

/// <summary>
/// A clock that tests control. Time moves only when a test moves it. Like every
/// <see cref="IClock"/>, it is always UTC and whole milliseconds.
/// </summary>
internal sealed class FakeClock : IClock
{
    /// <summary>An arbitrary fixed start, so tests never depend on the real date.</summary>
    public static readonly DateTimeOffset DefaultStart = new(2026, 1, 15, 9, 30, 0, TimeSpan.Zero);

    public FakeClock()
        : this(DefaultStart)
    {
    }

    public FakeClock(DateTimeOffset start)
    {
        EnsureUtc(start);
        UtcNow = start;
    }

    public DateTimeOffset UtcNow { get; private set; }

    /// <summary>Moves time forward. Time never moves backwards on its own.</summary>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        EnsureWholeMilliseconds(by.Ticks, nameof(by));
        UtcNow += by;
    }

    /// <summary>Sets the time to any UTC instant, including an earlier one.</summary>
    public void Set(DateTimeOffset value)
    {
        EnsureUtc(value);
        UtcNow = value;
    }

    private static void EnsureUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The fake clock works in UTC only.", nameof(value));
        }

        EnsureWholeMilliseconds(value.Ticks, nameof(value));
    }

    private static void EnsureWholeMilliseconds(long ticks, string parameterName)
    {
        if (ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new ArgumentException("The fake clock works in whole milliseconds only.", parameterName);
        }
    }
}
