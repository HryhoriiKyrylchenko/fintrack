using FinTrack.Budget.Domain.Settings;
using FinTrack.Shared.Kernel.Ledger;

namespace FinTrack.Budget.Application;

/// <summary>
/// The one place that reads Budget's state for a command or a query (ARCHITECTURE 2.3): the ledger
/// version first, then the documents. A command commits against <see cref="BudgetSnapshot.Version"/>,
/// so a write that lands in between makes the commit fail instead of passing on stale data.
/// </summary>
/// <remarks>Buckets (step 5) and the journal with the state builder (step 6) are added here.</remarks>
public sealed class BudgetStateLoader(ILedger ledger, IBudgetStore store)
{
    /// <summary>Reads the version, then the owner's documents.</summary>
    public async Task<BudgetSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        var version = await ledger.GetVersionAsync(BudgetLedger.Name, cancellationToken).ConfigureAwait(false);
        var settings = await store.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        return new BudgetSnapshot(version, settings);
    }
}

/// <summary>Budget's state for one owner, as read by <see cref="BudgetStateLoader"/>.</summary>
/// <param name="Version">The ledger version, read before everything else; the expected version of a commit.</param>
/// <param name="Settings">The owner's settings, or <c>null</c> before the first save.</param>
public sealed record BudgetSnapshot(long Version, OwnerSettings? Settings);
