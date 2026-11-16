using FinTrack.Shared.Kernel.Ledger;

namespace FinTrack.Budget.Application;

/// <summary>Budget's ledger: every Budget write is committed under its version, and its collections start with <c>budget_</c>.</summary>
public static class BudgetLedger
{
    /// <summary>The ledger name, <c>budget</c>.</summary>
    public static readonly LedgerName Name = LedgerName.From("budget");
}
