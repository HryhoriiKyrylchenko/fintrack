using FinTrack.Budget.Application.Buckets;
using FinTrack.Budget.Contracts.V1.Overview;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Loans;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Shared.Kernel.Time;
using FluentResults;
using Mediator;

namespace FinTrack.Budget.Application.Overview;

/// <summary>
/// Answers <see cref="GetBudgetOverview"/> from the replayed state (<see cref="BudgetStateLoader"/>,
/// <c>BudgetState</c>): one summary per active bucket, a total per currency, and the reporting total
/// (ARCHITECTURE 3, plan 1a D8).
/// </summary>
/// <remarks>
/// <para>
/// Each currency's subtotal is converted once, with banker's rounding (<see cref="FxRate.Convert"/>), and
/// the converted figures are summed in the reporting currency: rounding per bucket would let the total
/// drift from the subtotals it is made of. The reporting total carries the oldest rate date used.
/// </para>
/// <para>
/// A rate the provider cannot give leaves that currency's conversion out, and with it the reporting total,
/// with the provider's error code as the reason; the query itself still succeeds. Nothing is stored and
/// nothing is logged here (no amounts, balances or rates reach a log).
/// </para>
/// </remarks>
public sealed class GetBudgetOverviewHandler(BudgetStateLoader loader, IFxRateProvider rates, IClock clock)
    : IQueryHandler<GetBudgetOverview, Result<BudgetOverviewDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<BudgetOverviewDto>> Handle(GetBudgetOverview query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var loaded = await loader.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailed)
        {
            return loaded.ToResult<BudgetOverviewDto>();
        }

        var snapshot = loaded.Value;
        if (snapshot.Settings is not { } settings)
        {
            return Result.Fail<BudgetOverviewDto>(AppError.NotFound(BudgetErrorCodes.SettingsNotFound));
        }

        // One reading of the clock: the state and the overview's date are for the same day.
        var today = settings.Today(clock.UtcNow);
        var state = snapshot.StateAsOf(today);
        var reporting = settings.ReportingCurrency;
        var active = snapshot.Buckets
            .Where(bucket => bucket.Status == BucketStatus.Active)
            .OrderBy(bucket => bucket.CreatedAt)
            .ThenBy(bucket => bucket.Id)
            .ToList();

        IReadOnlyList<BucketSummaryDto> summaries = [.. active.Select(bucket => Summary(bucket, state))];

        var subtotals = summaries
            .GroupBy(summary => summary.Currency)
            .Select(group => group.Select(summary => summary.Available).Aggregate((sum, available) => sum + available))
            .OrderBy(subtotal => subtotal.Currency == reporting ? 0 : 1)
            .ThenBy(subtotal => subtotal.Currency.Code, StringComparer.Ordinal)
            .ToList();

        var totals = new List<CurrencyTotalDto>(subtotals.Count);
        var reportingAmount = Money.Zero(reporting);
        DateOnly? oldestRateDate = null;
        string? unavailableReason = null;

        foreach (var subtotal in subtotals)
        {
            if (subtotal.Currency == reporting)
            {
                totals.Add(new CurrencyTotalDto(subtotal.Currency, subtotal, Conversion: null));
                reportingAmount += subtotal;
                continue;
            }

            var rate = await rates.GetRateAsync(subtotal.Currency, reporting, cancellationToken).ConfigureAwait(false);
            if (rate.IsFailed)
            {
                totals.Add(new CurrencyTotalDto(subtotal.Currency, subtotal, Conversion: null));
                unavailableReason ??= ReasonOf(rate);
                continue;
            }

            var converted = rate.Value.Convert(subtotal);
            totals.Add(new CurrencyTotalDto(subtotal.Currency, subtotal, new ConversionDto(converted, rate.Value.Rate, rate.Value.Date)));
            reportingAmount += converted;
            oldestRateDate = oldestRateDate is { } date && date <= rate.Value.Date ? date : rate.Value.Date;
        }

        var reportingTotal = unavailableReason is null
            ? new ReportingTotalDto(reportingAmount, oldestRateDate, UnavailableReason: null)
            : new ReportingTotalDto(Amount: null, RateDate: null, unavailableReason);

        return Result.Ok(new BudgetOverviewDto(reporting, today, totals, reportingTotal, summaries));
    }

    private static BucketSummaryDto Summary(Bucket bucket, BudgetState state)
    {
        var balance = state.BalanceOf(bucket.BucketId);
        return new BucketSummaryDto(
            bucket.Id,
            bucket.Name,
            bucket.Currency,
            BucketContractMapping.ToContract(bucket.Kind),
            BucketContractMapping.ToContract(bucket.Profile),
            BucketContractMapping.ToContract(bucket.SystemRole),
            balance.Available,
            balance.NetContributed,
            balance.OutstandingPrincipal,
            state.Loans.Count(loan => loan.LenderBucketId == bucket.BucketId && loan.Status == LoanStatus.Open));
    }

    // The provider's own code (fx.rate_unavailable); a failure without one is a bug in the provider.
    private static string ReasonOf(IResultBase failed) =>
        failed.Errors.OfType<AppError>().FirstOrDefault()?.Code
        ?? throw new InvalidOperationException("The exchange rate provider failed without an error code.");
}
