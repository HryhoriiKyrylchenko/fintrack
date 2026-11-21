namespace FinTrack.Budget.Domain.Journal;

/// <summary>The direction of an <see cref="Adjustment"/>; stored and sent by name.</summary>
public enum AdjustmentDirection
{
    /// <summary>Raises <c>NetContributed</c>.</summary>
    Increase,

    /// <summary>Lowers <c>NetContributed</c>; held to invariant 1 on a savings bucket.</summary>
    Decrease,
}
