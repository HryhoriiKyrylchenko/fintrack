using FinTrack.Budget.Domain.Journal;

namespace FinTrack.Budget.Application.Journal;

/// <summary>
/// Whether effective entries still refer to an entry, so that it cannot be reversed (plan 1b B5,
/// <c>entry.has_dependents</c>): a <c>Borrow</c> with repayments or a repayment plan, a <c>PlanCreated</c> with
/// changes, pauses, waivers or linked contributions, a <c>PlanPaused</c> with changes. Reversed entries and
/// reversals never count.
/// </summary>
public interface IEntryDependencies
{
    /// <summary>Whether an effective entry of <paramref name="state"/> refers to <paramref name="entry"/>.</summary>
    /// <param name="entry">The entry to be reversed.</param>
    /// <param name="state">The owner's state the reversal is checked against.</param>
    bool HasDependents(JournalEntry entry, BudgetSnapshot state);
}

/// <summary>
/// The dependency rule of the entry types that exist. No type of phase 1a is referred to by another entry except
/// through a reversal (which is not effective), so none has dependents; the types that do get their case in the
/// step that adds them (loans step 5, plans step 9, pauses step 10). An unknown type is a bug: every new type must
/// decide here.
/// </summary>
public sealed class JournalEntryDependencies : IEntryDependencies
{
    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The entry's type has no case here yet (a bug).</exception>
    public bool HasDependents(JournalEntry entry, BudgetSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(state);

        return entry switch
        {
            OpeningBalance or Deposit or Spend or Transfer or Reversal => false,
            _ => throw new InvalidOperationException($"The dependency rule does not know the entry type {entry.GetType().Name}."),
        };
    }
}
