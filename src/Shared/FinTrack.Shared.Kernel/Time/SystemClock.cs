namespace FinTrack.Shared.Kernel.Time;

/// <summary>The real clock. The only place in the code base that reads the system time.</summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
