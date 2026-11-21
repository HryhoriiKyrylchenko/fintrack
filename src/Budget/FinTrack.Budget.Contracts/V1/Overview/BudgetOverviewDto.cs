using FinTrack.Budget.Contracts.V1.Buckets;
using FinTrack.Shared.Kernel.Monetary;

namespace FinTrack.Budget.Contracts.V1.Overview;

/// <summary>The answer to <see cref="GetBudgetOverview"/>, version 1 of the contract.</summary>
/// <param name="ReportingCurrency">The owner's reporting currency (a setting); <see cref="ReportingTotal"/> is in it. JSON: its code.</param>
/// <param name="AsOf">Today in the owner's time zone. Entries are never dated later, so every balance here is the balance at the end of this day so far.</param>
/// <param name="Totals">
/// One total per currency that has at least one active bucket: the reporting currency first (when it has a
/// bucket), then the others by currency code. Empty when the owner has no active bucket.
/// </param>
/// <param name="ReportingTotal">All of <paramref name="Totals"/> in the reporting currency, or why that total cannot be given.</param>
/// <param name="Buckets">
/// One summary per active bucket, <c>Unallocated</c> buckets included (also when they hold nothing), in the
/// order of <c>GetBuckets</c>: by creation, oldest first. Archived buckets are left out; they always hold zero.
/// </param>
public sealed record BudgetOverviewDto(
    Currency ReportingCurrency,
    DateOnly AsOf,
    IReadOnlyList<CurrencyTotalDto> Totals,
    ReportingTotalDto ReportingTotal,
    IReadOnlyList<BucketSummaryDto> Buckets);

/// <summary>What the owner's active buckets of one currency hold together.</summary>
/// <param name="Currency">The currency. JSON: its code.</param>
/// <param name="Available">
/// The sum of <see cref="BucketSummaryDto.Available"/> over the active buckets of this currency, in this
/// currency: savings buckets, the <c>Unallocated</c> bucket and investment buckets (their net invested
/// amount, not a market value; it may be below zero). Never converted.
/// </param>
/// <param name="Conversion">
/// <paramref name="Available"/> converted to the reporting currency, with the rate used; <c>null</c> for the
/// reporting currency itself and when no rate is known for this currency.
/// </param>
public sealed record CurrencyTotalDto(Currency Currency, Money Available, ConversionDto? Conversion);

/// <summary>One currency's total converted to the reporting currency, computed at read time and never stored.</summary>
/// <param name="Amount">The converted amount in the reporting currency, rounded once with banker's rounding to its precision.</param>
/// <param name="Rate">Units of the reporting currency per unit of the converted currency, unrounded (a cross rate through EUR).</param>
/// <param name="RateDate">The day the rate was published for; it may be some days old (weekends, offline).</param>
public sealed record ConversionDto(Money Amount, decimal Rate, DateOnly RateDate);

/// <summary>
/// The total of every currency in the reporting currency: either <see cref="Amount"/> with
/// <see cref="RateDate"/>, or <see cref="UnavailableReason"/>; never both.
/// </summary>
/// <param name="Amount">
/// The sum of the reporting currency's own total and every other currency's
/// <see cref="CurrencyTotalDto.Conversion"/>; zero when there are no buckets. <c>null</c> when a rate is missing,
/// because a total without one of its currencies would be wrong.
/// </param>
/// <param name="RateDate">
/// The oldest rate date among the conversions, so the total is never presented as newer than its oldest
/// rate; <c>null</c> when nothing had to be converted, or when <paramref name="Amount"/> is <c>null</c>.
/// </param>
/// <param name="UnavailableReason">
/// The error code that explains a missing <paramref name="Amount"/>: <c>fx.rate_unavailable</c> when no rate
/// is known for some currency (the source cannot be reached and none is stored); otherwise <c>null</c>.
/// </param>
public sealed record ReportingTotalDto(Money? Amount, DateOnly? RateDate, string? UnavailableReason);

/// <summary>One active bucket's definition and balances (ARCHITECTURE 4.3), all in the bucket's currency.</summary>
/// <param name="Id">The bucket's ID (a GUID string in JSON).</param>
/// <param name="Name">The name; <c>null</c> for a system bucket, whose name comes from the UI resources.</param>
/// <param name="Currency">The bucket's currency; every amount of this summary is in it. JSON: its code.</param>
/// <param name="Kind">Savings or investment.</param>
/// <param name="Profile">The profile of an investment bucket; <c>null</c> for a savings bucket.</param>
/// <param name="SystemRole"><c>None</c> for the user's buckets, <c>Unallocated</c> for the system bucket of its currency.</param>
/// <param name="Available">
/// <paramref name="NetContributed"/> − <paramref name="OutstandingPrincipal"/>: what can be spent or moved now. Never below
/// zero for a savings bucket (invariant 1). For an investment bucket this is the net invested amount ("Invested"),
/// which may be below zero; market value arrives with the Investments context (phase 3).
/// </param>
/// <param name="NetContributed">
/// Opening balance + deposits + transfers in − spends − transfers out + adjustments up − adjustments down + borrowed into the
/// bucket − repayments paid from it; reversed entries do not count.
/// </param>
/// <param name="OutstandingPrincipal">Principal lent from this bucket and not repaid yet: the sum of its open loans' <c>outstanding</c>.</param>
/// <param name="OpenLoans">How many loans from this bucket are open (plan 1b B19); <c>GET /api/v1/loans</c> lists them.</param>
public sealed record BucketSummaryDto(
    Guid Id,
    string? Name,
    Currency Currency,
    BucketKind Kind,
    InvestmentProfile? Profile,
    BucketSystemRole SystemRole,
    Money Available,
    Money NetContributed,
    Money OutstandingPrincipal,
    int OpenLoans = 0);
