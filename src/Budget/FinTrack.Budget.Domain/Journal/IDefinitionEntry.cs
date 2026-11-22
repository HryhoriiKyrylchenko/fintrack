namespace FinTrack.Budget.Domain.Journal;

/// <summary>
/// An entry that defines a plan, a pause, a limit or a repayment plan and moves no money (plan 1b B1). Every definition
/// entry, and every reversal of one, is held to invariant 6: it may not change what any plan accrued in a month before
/// the current month (B8, <see cref="BudgetErrorCodes.PlanPastPeriodChanged"/>).
/// </summary>
public interface IDefinitionEntry;
