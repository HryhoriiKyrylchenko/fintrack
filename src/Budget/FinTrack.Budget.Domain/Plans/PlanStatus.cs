namespace FinTrack.Budget.Domain.Plans;

/// <summary>
/// A plan's status in the current month (plan 1b B7), derived. <c>LimitReached</c> (step 11) joins when limits exist;
/// <c>DueNow</c> and <c>Shortfall</c> are separate, so an ended plan with a shortfall stays visible.
/// </summary>
public enum PlanStatus
{
    /// <summary>The plan accrues in the current month, or will from a later month.</summary>
    Active,

    /// <summary>A pause covers the current month (plan 1b B10).</summary>
    Paused,

    /// <summary>The plan's last accruing month has passed; it may still have a shortfall.</summary>
    Ended,
}
