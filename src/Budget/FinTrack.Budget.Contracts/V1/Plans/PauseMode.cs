namespace FinTrack.Budget.Contracts.V1.Plans;

/// <summary>
/// How a pause treats its months (plan 1b B10). JSON: the member's name, which is also the stored name; a copy of the domain's enum
/// with the same member names (Contracts never references Domain).
/// </summary>
public enum PauseMode
{
    /// <summary>Paused months accrue nothing and the plan's end moves later by them.</summary>
    Shift,

    /// <summary>Paused months accrue nothing and are dropped; the plan's end stays.</summary>
    Skip,
}
