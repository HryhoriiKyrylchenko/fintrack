namespace FinTrack.Budget.Contracts.V1.Journal;

/// <summary>
/// The direction of an <c>Adjustment</c> (plan 1b B14). JSON: the member's name, which is also the stored name;
/// a copy of the domain's enum with the same member names (Contracts never references Domain).
/// </summary>
public enum AdjustmentDirection
{
    /// <summary>Raises the bucket's <c>netContributed</c>.</summary>
    Increase,

    /// <summary>Lowers the bucket's <c>netContributed</c>; a savings bucket never goes below zero.</summary>
    Decrease,
}
