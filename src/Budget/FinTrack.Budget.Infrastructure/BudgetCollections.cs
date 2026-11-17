namespace FinTrack.Budget.Infrastructure;

/// <summary>Budget's own collections (ARCHITECTURE 2.2). The ledger's collections (<c>budget_ledger_heads</c> and so on) are named by the shared ledger.</summary>
public static class BudgetCollections
{
    /// <summary>One <c>OwnerSettings</c> document per owner.</summary>
    public const string Settings = "budget_settings";

    /// <summary>The owner's buckets, archived ones included.</summary>
    public const string Buckets = "budget_buckets";
}
