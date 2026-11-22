using FinTrack.Budget.Domain.Buckets;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Time;

namespace FinTrack.Budget.Domain.Plans;

/// <summary>
/// A contribution plan (ARCHITECTURE 4.5, plan 1b B7), derived by the state builder from its <c>PlanCreated</c>, its
/// effective changes and the contributions linked to it. Never stored. Every amount is in the bucket's currency. What it
/// accrues and owes is computed by <see cref="PlanSchedule"/>.
/// </summary>
/// <param name="PlanId">The plan: its <c>PlanCreated</c> entry's ID.</param>
/// <param name="BucketId">The bucket it saves into.</param>
/// <param name="ValidFrom">The first month that accrues.</param>
/// <param name="ValidTo">The last month that accrues, or <c>null</c> without an end (the latest <c>PlanEndChanged</c> wins).</param>
/// <param name="CreatedOn">The business date of its <c>PlanCreated</c>.</param>
/// <param name="Versions">Its amounts, in replay order: the first from <paramref name="ValidFrom"/>, then each <c>PlanAmountChanged</c>.</param>
/// <param name="Contributed">The sum of the effective deposits and transfers in linked to it (plan 1b B9).</param>
public sealed record Plan(
    PlanId PlanId,
    BucketId BucketId,
    YearMonth ValidFrom,
    YearMonth? ValidTo,
    DateOnly CreatedOn,
    IReadOnlyList<PlanVersion> Versions,
    Money Contributed)
{
    /// <summary>The plan's currency: its bucket's.</summary>
    public Currency Currency => Contributed.Currency;

    /// <summary>
    /// The amount of the version in force in <paramref name="month"/>: the one with the latest <c>from</c> at or before it,
    /// the later in replay order on a tie; zero before every version. Whether the month accrues at all is
    /// <see cref="PlanSchedule.Accrual"/>.
    /// </summary>
    public Money AmountIn(YearMonth month)
    {
        PlanVersion? inForce = null;
        foreach (var version in Versions)
        {
            if (version.From <= month && (inForce is null || version.From >= inForce.From))
            {
                inForce = version;
            }
        }

        return inForce?.Amount ?? Money.Zero(Currency);
    }
}

/// <summary>One version of a plan's monthly amount.</summary>
/// <param name="From">Its first month.</param>
/// <param name="Amount">The monthly amount, above zero.</param>
public sealed record PlanVersion(YearMonth From, Money Amount);
