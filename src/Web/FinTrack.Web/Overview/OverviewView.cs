using FinTrack.Budget.Contracts.V1.Buckets;
using FinTrack.Budget.Contracts.V1.Overview;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Web.Api;

namespace FinTrack.Web.Overview;

/// <summary>The overview endpoint of the Api (<c>GET /api/v1/overview</c>), through <see cref="ApiClient"/>.</summary>
public sealed class OverviewClient(ApiClient api)
{
    /// <summary>Totals and one summary per active bucket; <c>settings.not_found</c> before the first run.</summary>
    public Task<ApiResult<BudgetOverviewDto>> GetAsync(CancellationToken cancellationToken) =>
        api.GetAsync<BudgetOverviewDto>("v1/overview", cancellationToken);
}

/// <summary>
/// What the Overview page shows (plan 1a step 15, DESIGN 8): the total in the reporting currency as the key
/// figure with its rate date (DESIGN 6, 13.4), or the reason it is missing; the total of every currency when
/// there is more than the reporting one; and one card per bucket. Nothing is computed here: every figure is
/// the Api's, so the page shows exactly what the read contract says.
/// </summary>
public sealed class OverviewView
{
    private OverviewView(BudgetOverviewDto overview, IReadOnlyList<OverviewCard> cards)
    {
        ReportingCurrency = overview.ReportingCurrency;
        Total = overview.ReportingTotal.Amount;
        RateDate = overview.ReportingTotal.Amount is null ? null : overview.ReportingTotal.RateDate;
        TotalUnavailableReason = overview.ReportingTotal.UnavailableReason;
        CurrencyTotals = overview.Totals;
        Cards = cards;
    }

    /// <summary>The owner's reporting currency.</summary>
    public Currency ReportingCurrency { get; }

    /// <summary>Everything in the reporting currency, or <c>null</c> when a rate is missing.</summary>
    public Money? Total { get; }

    /// <summary>The oldest rate date behind <see cref="Total"/>; <c>null</c> when nothing was converted.</summary>
    public DateOnly? RateDate { get; }

    /// <summary>The error code that explains a missing <see cref="Total"/> (<c>fx.rate_unavailable</c>), or <c>null</c>.</summary>
    public string? TotalUnavailableReason { get; }

    /// <summary>The total of each currency, reporting currency first, each in its own currency.</summary>
    public IReadOnlyList<CurrencyTotalDto> CurrencyTotals { get; }

    /// <summary>
    /// Whether the currency totals are worth showing: only when a currency other than the reporting one has
    /// buckets; otherwise they would repeat <see cref="Total"/>.
    /// </summary>
    public bool ShowCurrencyTotals => CurrencyTotals.Any(total => total.Currency != ReportingCurrency);

    /// <summary>One card per active bucket in the Api's order; an <c>Unallocated</c> bucket only while it holds money.</summary>
    public IReadOnlyList<OverviewCard> Cards { get; }

    /// <summary>Whether there is nothing to show yet: no bucket of the owner and no money waiting in <c>Unallocated</c>.</summary>
    public bool IsEmpty => Cards.Count == 0;

    /// <summary>The view of <paramref name="overview"/>.</summary>
    public static OverviewView From(BudgetOverviewDto overview)
    {
        ArgumentNullException.ThrowIfNull(overview);

        IReadOnlyList<OverviewCard> cards =
        [
            .. overview.Buckets
                .Where(bucket => bucket.SystemRole == BucketSystemRole.None || bucket.Available.Amount != 0m)
                .Select(bucket => new OverviewCard(bucket)),
        ];

        return new OverviewView(overview, cards);
    }
}

/// <summary>One bucket's card on the Overview.</summary>
/// <param name="Bucket">The bucket and its balances.</param>
public sealed record OverviewCard(BucketSummaryDto Bucket)
{
    /// <summary>Resource key of the label of a savings bucket's figure.</summary>
    public const string AvailableKey = "Overview.Available";

    /// <summary>Resource key of the label of an investment bucket's figure: the net invested amount (no market value before phase 3).</summary>
    public const string InvestedKey = "Overview.Invested";

    /// <summary>The figure the card shows: <c>Available</c> (for an investment bucket the net invested amount, which may be below zero).</summary>
    public Money Amount => Bucket.Available;

    /// <summary>The resource key of the figure's label.</summary>
    public string AmountLabelKey => Bucket.Kind == BucketKind.Investment ? InvestedKey : AvailableKey;
}
