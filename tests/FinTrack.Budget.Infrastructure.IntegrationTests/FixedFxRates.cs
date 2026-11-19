using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FluentResults;

namespace FinTrack.Budget.Infrastructure.IntegrationTests;

/// <summary>
/// The exchange rates of the Budget integration tests, without network or database: EUR to PLN only (an
/// invented rate); every other pair fails with <c>fx.rate_unavailable</c>, as the real provider does when
/// it has nothing stored. The provider itself is tested in the shared infrastructure tests.
/// </summary>
internal sealed class FixedFxRates : IFxRateProvider
{
    public static readonly FxRate EurToPln = new(Currency.Eur, Currency.Pln, 4.2735m, new DateOnly(2026, 1, 14));

    public static readonly FixedFxRates Instance = new();

    private FixedFxRates()
    {
    }

    public Task<Result<FxRate>> GetRateAsync(Currency from, Currency to, CancellationToken cancellationToken) =>
        Task.FromResult(from == EurToPln.From && to == EurToPln.To
            ? Result.Ok(EurToPln)
            : Result.Fail<FxRate>(AppError.Unavailable(ErrorCodes.FxRateUnavailable)));
}
