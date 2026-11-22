using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Time;

namespace FinTrack.Budget.Domain.Plans;

/// <summary>
/// The period engine of contribution plans (ARCHITECTURE 4.5, plan 1b B7, B8). Pure: a function of the plan and the
/// current month.
/// </summary>
/// <remarks>
/// <para>
/// Periods are calendar months. A month accrues on its first day, so the current month is due during the month. Month
/// M accrues the amount of the version in force (<see cref="Plan.AmountIn"/>) when M lies between <c>ValidFrom</c> and
/// <c>ValidTo</c>, and nothing otherwise. Steps 10 and 11 add paused months and the limit cap here, in
/// <see cref="Accrual"/>, and waivers to the credit.
/// </para>
/// <para>
/// Credit (the linked contributions) settles the oldest accrued month first, whatever the contributions' dates, so the
/// current month is the last to be paid. <c>DueNow</c> = accrued through the current month - credit (never below zero);
/// <c>Shortfall</c> = the part of it from months before the current one; <c>Prepaid</c> = credit beyond everything
/// accrued, which settles later months as they accrue. 4.5's example: 500 a month, 300 paid in month 1, so month 2 has
/// 700 due, 200 of it a shortfall.
/// </para>
/// </remarks>
public static class PlanSchedule
{
    /// <summary>What <paramref name="plan"/> accrues in <paramref name="month"/>.</summary>
    public static Money Accrual(Plan plan, YearMonth month)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return month < plan.ValidFrom || month > plan.ValidTo
            ? Money.Zero(plan.Currency)
            : plan.AmountIn(month);
    }

    /// <summary>Where <paramref name="plan"/> stands in <paramref name="currentMonth"/> (the month of the owner's today).</summary>
    public static PlanStanding Standing(Plan plan, YearMonth currentMonth)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var zero = Money.Zero(plan.Currency);
        var credit = plan.Contributed;
        var remaining = credit;
        var periods = new List<PlanPeriod>();
        var accrued = zero;
        var accruedBefore = zero;

        var last = plan.ValidTo is { } end && end < currentMonth ? end : currentMonth;
        for (var month = plan.ValidFrom; month <= last; month = month.Next())
        {
            var accrual = Accrual(plan, month);
            var settled = remaining < accrual ? remaining : accrual;
            remaining -= settled;
            periods.Add(new PlanPeriod(month, accrual, settled));

            accrued += accrual;
            if (month < currentMonth)
            {
                accruedBefore += accrual;
            }

            if (month == YearMonth.MaxValue)
            {
                break;
            }
        }

        var dueNow = Max(accrued - credit, zero);
        var status = plan.ValidTo < currentMonth ? PlanStatus.Ended : PlanStatus.Active;

        return new PlanStanding(
            plan.PlanId,
            currentMonth,
            accrued,
            credit,
            dueNow,
            Shortfall: Max(accruedBefore - credit, zero),
            Prepaid: Max(credit - accrued, zero),
            status,
            IsActive: status == PlanStatus.Active || dueNow.Amount > 0m,
            periods);
    }

    /// <summary>
    /// The first month before <paramref name="currentMonth"/> in which a plan of <paramref name="before"/> accrues
    /// differently in <paramref name="after"/> (a plan missing there accrues nothing), or <c>null</c> when none does
    /// (invariant 6, plan 1b B8). Plans only in <paramref name="after"/> are new: they have no periods yet to change.
    /// </summary>
    public static YearMonth? FirstChangedPastMonth(IEnumerable<Plan> before, IReadOnlyCollection<Plan> after, YearMonth currentMonth)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        YearMonth? first = null;
        foreach (var earlier in before)
        {
            var later = after.FirstOrDefault(plan => plan.PlanId == earlier.PlanId);
            for (var month = earlier.ValidFrom; month < currentMonth && (first is null || month < first); month = month.Next())
            {
                var was = Accrual(earlier, month).Amount;
                var now = later is null ? 0m : Accrual(later, month).Amount;
                if (was != now)
                {
                    first = month;
                    break;
                }
            }
        }

        return first;
    }

    private static Money Max(Money left, Money right) => left >= right ? left : right;
}

/// <summary>Where a plan stands in the current month (plan 1b B7). Every amount is in the plan's currency.</summary>
/// <param name="PlanId">The plan.</param>
/// <param name="CurrentMonth">The month it is computed for: the month of the owner's today.</param>
/// <param name="Accrued">Everything accrued from its first month through the current one (or its end, if earlier).</param>
/// <param name="Credit">The linked contributions (waivers join in step 10).</param>
/// <param name="DueNow"><paramref name="Accrued"/> - <paramref name="Credit"/>, never below zero: what to pay now, the current month included.</param>
/// <param name="Shortfall">The part of <paramref name="DueNow"/> from months before the current one: carried over, never a loan.</param>
/// <param name="Prepaid">Credit beyond everything accrued: it settles later months as they accrue.</param>
/// <param name="Status">Active, or ended once its last accruing month has passed.</param>
/// <param name="IsActive">Whether it blocks archiving its bucket (B17): it accrues in the current month or later, or has money due.</param>
/// <param name="Periods">Each accrued month, oldest first, with what it accrued and what the credit settled of it.</param>
public sealed record PlanStanding(
    PlanId PlanId,
    YearMonth CurrentMonth,
    Money Accrued,
    Money Credit,
    Money DueNow,
    Money Shortfall,
    Money Prepaid,
    PlanStatus Status,
    bool IsActive,
    IReadOnlyList<PlanPeriod> Periods);

/// <summary>One accrued month of a plan.</summary>
/// <param name="Month">The month.</param>
/// <param name="Accrued">What it accrued.</param>
/// <param name="Settled">What the credit settled of it, oldest months first.</param>
public sealed record PlanPeriod(YearMonth Month, Money Accrued, Money Settled)
{
    /// <summary>What is still unpaid of this month.</summary>
    public Money Unpaid => Accrued - Settled;
}
