namespace FinTrack.Shared.Kernel.Time;

/// <summary>
/// The only source of the current time. Domain and Application code never read the
/// system clock directly (CLAUDE.md rule 11), so tests can control time.
/// </summary>
public interface IClock
{
    /// <summary>The current instant, always with a zero offset (UTC).</summary>
    DateTimeOffset UtcNow { get; }
}
