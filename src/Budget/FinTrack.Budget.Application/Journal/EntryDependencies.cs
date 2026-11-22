using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.State;

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
/// The dependency rule of the entry types that exist. A <c>Borrow</c> has dependents while an effective <c>Repayment</c>
/// of its loan exists (plan 1b step 5); a <c>PlanCreated</c> while an effective change of its plan or a deposit or transfer
/// linked to it exists (step 9), or a pause or waiver of it (step 10); a <c>PlanPaused</c> while an effective change of its pause
/// exists (step 10). No type of phase 1a, no <c>Adjustment</c> (step 4) and no <c>Repayment</c> is
/// referred to by another entry except through a reversal (which is not effective), so none has dependents; the types that do get their case in the
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
            Borrow borrow => HasEffectiveRepayment(borrow, state),
            PlanCreated created => HasEffectivePlanEntry(created, state),
            PlanPaused paused => HasEffectivePauseChange(paused, state),
            OpeningBalance or Deposit or Spend or Transfer or Reversal or Adjustment or Repayment or PlanAmountChanged or PlanEndChanged
                or PlanPauseChanged or ShortfallWaived => false,
            _ => throw new InvalidOperationException($"The dependency rule does not know the entry type {entry.GetType().Name}."),
        };
    }

    // A borrow with an effective repayment (B5); a repayment plan (step 12) will count too.
    private static bool HasEffectiveRepayment(Borrow borrow, BudgetSnapshot state)
    {
        var reversed = BudgetState.ReversedEntryIds(state.Journal);
        return state.Journal.OfType<Repayment>().Any(repayment => repayment.LoanId == borrow.LoanId && !reversed.Contains(repayment.Id));
    }

    // A plan with an effective change, pause, waiver or linked contribution (B5).
    private static bool HasEffectivePlanEntry(PlanCreated created, BudgetSnapshot state)
    {
        var reversed = BudgetState.ReversedEntryIds(state.Journal);
        return state.Journal.Any(entry => !reversed.Contains(entry.Id) && entry switch
        {
            PlanAmountChanged changed => changed.PlanId == created.PlanId,
            PlanEndChanged ended => ended.PlanId == created.PlanId,
            PlanPaused paused => paused.PlanId == created.PlanId,
            ShortfallWaived waived => waived.PlanId == created.PlanId,
            Deposit deposit => deposit.PlanId == created.PlanId,
            Transfer transfer => transfer.PlanId == created.PlanId,
            _ => false,
        });
    }

    // A pause with an effective change of it (B5).
    private static bool HasEffectivePauseChange(PlanPaused paused, BudgetSnapshot state)
    {
        var reversed = BudgetState.ReversedEntryIds(state.Journal);
        return state.Journal.OfType<PlanPauseChanged>().Any(changed => changed.PauseId == paused.PauseId && !reversed.Contains(changed.Id));
    }
}
