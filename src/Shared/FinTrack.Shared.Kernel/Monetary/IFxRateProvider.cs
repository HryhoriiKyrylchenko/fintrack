using FinTrack.Shared.Kernel.Results;
using FluentResults;

namespace FinTrack.Shared.Kernel.Monetary;

/// <summary>
/// Fiat exchange rates for totals across currencies (ARCHITECTURE 3, plan 1a D8). Declared here
/// because every context needs it; implemented in <c>FinTrack.Shared.Infrastructure</c>.
/// Never called from Domain.
/// </summary>
public interface IFxRateProvider
{
    /// <summary>
    /// The newest rate from <paramref name="from"/> to <paramref name="to"/>: one fetched today, a
    /// fresh one from the source, or, when the source cannot be reached, the newest stored one; its
    /// <see cref="FxRate.Date"/> says which day it is for. Fails with <c>Unavailable</c>
    /// <see cref="ErrorCodes.FxRateUnavailable"/> when there is none at all. Never fails because the
    /// source is down while a stored rate exists.
    /// </summary>
    Task<Result<FxRate>> GetRateAsync(Currency from, Currency to, CancellationToken cancellationToken);
}
