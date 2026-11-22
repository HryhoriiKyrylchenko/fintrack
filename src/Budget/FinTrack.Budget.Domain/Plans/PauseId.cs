using FinTrack.Shared.Kernel.Ids;

namespace FinTrack.Budget.Domain.Plans;

/// <summary>
/// Identifies a pause of a plan (plan 1b B10): the ID of the <c>PlanPaused</c> entry that started it. Derived by replay, never
/// stored on its own.
/// </summary>
public readonly record struct PauseId : IStronglyTypedId<PauseId>
{
    private PauseId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static PauseId From(Guid value) =>
        value == Guid.Empty ? throw new ArgumentException("A pause identifier cannot be empty.", nameof(value)) : new PauseId(value);

    /// <summary>The identifier in the standard <c>D</c> format.</summary>
    public override string ToString() => Value.ToString();
}
