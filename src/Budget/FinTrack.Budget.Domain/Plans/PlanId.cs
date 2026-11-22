using FinTrack.Shared.Kernel.Ids;

namespace FinTrack.Budget.Domain.Plans;

/// <summary>
/// Identifies a contribution plan (ARCHITECTURE 4.5, plan 1b B1): the ID of the <c>PlanCreated</c> entry that created it.
/// A plan is never stored on its own; it is derived by replaying its definition entries and linked contributions.
/// </summary>
public readonly record struct PlanId : IStronglyTypedId<PlanId>
{
    private PlanId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static PlanId From(Guid value) =>
        value == Guid.Empty ? throw new ArgumentException("A plan identifier cannot be empty.", nameof(value)) : new PlanId(value);

    /// <summary>The identifier in the standard <c>D</c> format.</summary>
    public override string ToString() => Value.ToString();
}
