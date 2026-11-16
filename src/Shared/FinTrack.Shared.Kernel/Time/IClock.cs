namespace FinTrack.Shared.Kernel.Time;

/// <summary>
/// The only source of the current time. Domain and Application code never read the
/// system clock directly (CLAUDE.md rule 11), so tests can control time.
/// </summary>
/// <remarks>
/// Instants are stored as BSON dates, which hold whole milliseconds. The clock therefore never
/// returns anything finer, so a value read back from the database equals the value written
/// (ARCHITECTURE 2.2). Storing an instant with a finer part is a bug and throws.
/// </remarks>
public interface IClock
{
    /// <summary>The current instant: always UTC (zero offset), always whole milliseconds.</summary>
    DateTimeOffset UtcNow { get; }
}
