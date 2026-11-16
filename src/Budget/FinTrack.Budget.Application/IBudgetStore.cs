using FinTrack.Budget.Domain.Settings;

namespace FinTrack.Budget.Application;

/// <summary>
/// Reads Budget's stored documents for the current owner. Implemented in Budget.Infrastructure on
/// the owner-scoped collections; writes never go through here, only through <c>ILedger</c>.
/// Use it only through <see cref="BudgetStateLoader"/>, which reads the ledger version first.
/// </summary>
public interface IBudgetStore
{
    /// <summary>The owner's settings, or <c>null</c> before they are saved for the first time.</summary>
    Task<OwnerSettings?> GetSettingsAsync(CancellationToken cancellationToken);
}
