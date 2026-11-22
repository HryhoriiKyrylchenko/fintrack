namespace FinTrack.Budget.Domain.Plans;

/// <summary>How a pause treats its months (ARCHITECTURE 4.5, plan 1b B10); stored and sent by name.</summary>
public enum PauseMode
{
    /// <summary>Paused months do not accrue and do not count towards the plan's months: the end moves later by them.</summary>
    Shift,

    /// <summary>Paused months do not accrue but count towards the plan's months: they are dropped and the end stays.</summary>
    Skip,
}
