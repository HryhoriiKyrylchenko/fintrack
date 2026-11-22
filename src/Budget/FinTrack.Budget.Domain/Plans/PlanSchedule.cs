using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Time;

namespace FinTrack.Budget.Domain.Plans;

/// <summary>
/// The period engine of contribution plans (ARCHITECTURE 4.5, plan 1b B7, B8, B10, B11). Pure: a function of the plan and the
/// current month.
/// </summary>
/// <remarks>
/// <para>
/// Periods are calendar months. A month accrues on its first day, so the current month is due during the month. Month M accrues
/// the amount of the version in force (<see cref="Plan.AmountIn"/>) when M lies between <c>ValidFrom</c> and the effective end
/// (<see cref="EffectiveEnd"/>) and no pause covers it; nothing otherwise. Step 11 adds the limit cap here, in
/// <see cref="Accrual"/>.
/// </para>
/// <para>
/// Pauses (B10): a plan with an end accrues as many months as <c>ValidFrom</c>..<c>ValidTo</c> holds. A month of a
/// <see cref="PauseMode.Skip"/> pause uses up one of them and accrues nothing, so the end stays; a month of a
/// <see cref="PauseMode.Shift"/> pause uses up none, so the end moves later by the paused months. A shift pause without a last month
/// leaves the end unknown (<c>null</c>). A plan without an end has no end to move.
/// </para>
/// <para>
/// Credit settles the oldest accrued month first, whatever the dates, so the current month is the last to be paid. It is the
/// linked contributions plus the effective part of the waivers (B11): each waiver, in replay order, settles at most what the months
/// before its own month still lack after the contributions and the waivers before it; the rest is void. So a waiver never covers
/// its own month or a later one and never creates a prepayment, and a backdated contribution voids what is no longer needed.
/// <c>DueNow</c> = accrued through the current month - credit (never below zero); <c>Shortfall</c> = the part of it from months
/// before the current one; <c>Prepaid</c> = credit beyond everything accrued, which settles later months as they accrue. 4.5's
/// example: 500 a month, 300 paid in month 1, so month 2 has 700 due, 200 of it a shortfall.
/// </para>
/// </remarks>
public static class PlanSchedule
{
    /// <summary>What <paramref name="plan"/> accrues in <paramref name="month"/>.</summary>
    public static Money Accrual(Plan plan, YearMonth month)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return AccrualUntil(plan, month, EffectiveEnd(plan));
    }

    /// <summary>
    /// The last month <paramref name="plan"/> accrues in, after shift pauses (B10): <c>ValidTo</c> moved later by the months of shift
    /// pauses inside the plan's months; <c>null</c> without an end, or while a shift pause inside them has no last month.
    /// </summary>
    public static YearMonth? EffectiveEnd(Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.ValidTo is not { } end)
        {
            return null;
        }

        if (end < plan.ValidFrom)
        {
            return end;
        }

        var months = YearMonth.MonthsBetween(plan.ValidFrom, end) + 1;
        var month = plan.ValidFrom;
        while (true)
        {
            if (plan.PauseIn(month) is { Mode: PauseMode.Shift } shift)
            {
                if (shift.To is not { } last || last == YearMonth.MaxValue)
                {
                    return null;
                }

                month = last.Next();
                continue;
            }

            months--;
            if (months == 0 || month == YearMonth.MaxValue)
            {
                return month;
            }

            month = month.Next();
        }
    }

    /// <summary>Where <paramref name="plan"/> stands in <paramref name="currentMonth"/> (the month of the owner's today).</summary>
    public static PlanStanding Standing(Plan plan, YearMonth currentMonth)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var zero = Money.Zero(plan.Currency);
        var end = EffectiveEnd(plan);
        var waived = EffectiveWaivers(plan, end);
        var credit = plan.Contributed + waived;
        var remaining = credit;
        var periods = new List<PlanPeriod>();
        var accrued = zero;
        var accruedBefore = zero;

        var last = end is { } effective && effective < currentMonth ? effective : currentMonth;
        for (var month = plan.ValidFrom; month <= last; month = month.Next())
        {
            var accrual = AccrualUntil(plan, month, end);
            var settled = remaining < accrual ? remaining : accrual;
            remaining -= settled;
            periods.Add(new PlanPeriod(month, accrual, settled, plan.PauseIn(month)?.Mode));

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
        var status = end < currentMonth ? PlanStatus.Ended
            : plan.PauseIn(currentMonth) is not null ? PlanStatus.Paused
            : PlanStatus.Active;

        return new PlanStanding(
            plan.PlanId,
            currentMonth,
            accrued,
            credit,
            waived,
            dueNow,
            Shortfall: Max(accruedBefore - credit, zero),
            Prepaid: Max(credit - accrued, zero),
            status,
            IsActive: status != PlanStatus.Ended || dueNow.Amount > 0m,
            end,
            periods);
    }

    /// <summary>
    /// What a new waiver dated in <paramref name="month"/> may write off at most (B11): what the months before it still lack after the
    /// contributions and the effective part of the existing waivers.
    /// </summary>
    public static Money ShortfallAt(Plan plan, YearMonth month)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var end = EffectiveEnd(plan);
        return Max(AccruedBefore(plan, month, end) - plan.Contributed - EffectiveWaivers(plan, end), Money.Zero(plan.Currency));
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
            var earlierEnd = EffectiveEnd(earlier);
            var laterEnd = later is null ? null : EffectiveEnd(later);
            for (var month = earlier.ValidFrom; month < currentMonth && (first is null || month < first); month = month.Next())
            {
                var was = AccrualUntil(earlier, month, earlierEnd).Amount;
                var now = later is null ? 0m : AccrualUntil(later, month, laterEnd).Amount;
                if (was != now)
                {
                    first = month;
                    break;
                }
            }
        }

        return first;
    }

    private static Money AccrualUntil(Plan plan, YearMonth month, YearMonth? end) =>
        month < plan.ValidFrom || month > end || plan.PauseIn(month) is not null
            ? Money.Zero(plan.Currency)
            : plan.AmountIn(month);

    // Everything accrued in the months before "month".
    private static Money AccruedBefore(Plan plan, YearMonth month, YearMonth? end)
    {
        var sum = Money.Zero(plan.Currency);
        for (var current = plan.ValidFrom; current < month; current = current.Next())
        {
            sum += AccrualUntil(plan, current, end);
        }

        return sum;
    }

    // The effective part of the waivers, in replay order: each at most what the months before its own month still lack.
    private static Money EffectiveWaivers(Plan plan, YearMonth? end)
    {
        var zero = Money.Zero(plan.Currency);
        var effective = zero;
        foreach (var waiver in plan.Waivers)
        {
            var room = Max(AccruedBefore(plan, waiver.Month, end) - plan.Contributed - effective, zero);
            effective += waiver.Amount < room ? waiver.Amount : room;
        }

        return effective;
    }

    private static Money Max(Money left, Money right) => left >= right ? left : right;
}

/// <summary>Where a plan stands in the current month (plan 1b B7). Every amount is in the plan's currency.</summary>
/// <param name="PlanId">The plan.</param>
/// <param name="CurrentMonth">The month it is computed for: the month of the owner's today.</param>
/// <param name="Accrued">Everything accrued from its first month through the current one (or its effective end, if earlier).</param>
/// <param name="Credit">The linked contributions plus <paramref name="Waived"/>.</param>
/// <param name="Waived">The effective part of its waivers (B11).</param>
/// <param name="DueNow"><paramref name="Accrued"/> - <paramref name="Credit"/>, never below zero: what to pay now, the current month included.</param>
/// <param name="Shortfall">The part of <paramref name="DueNow"/> from months before the current one: carried over, never a loan.</param>
/// <param name="Prepaid">Credit beyond everything accrued: it settles later months as they accrue.</param>
/// <param name="Status">Active, paused in the current month, or ended once its last accruing month has passed.</param>
/// <param name="IsActive">Whether it blocks archiving its bucket (B17): it has not ended, or it has money due.</param>
/// <param name="EffectiveEnd">The last month it accrues in after shift pauses, or <c>null</c> (no end, or not known yet).</param>
/// <param name="Periods">Each month from its first through the current one (or its effective end), oldest first: what it accrued, what the credit settled, and the mode of a pause covering it.</param>
public sealed record PlanStanding(
    PlanId PlanId,
    YearMonth CurrentMonth,
    Money Accrued,
    Money Credit,
    Money Waived,
    Money DueNow,
    Money Shortfall,
    Money Prepaid,
    PlanStatus Status,
    bool IsActive,
    YearMonth? EffectiveEnd,
    IReadOnlyList<PlanPeriod> Periods);

/// <summary>One month of a plan.</summary>
/// <param name="Month">The month.</param>
/// <param name="Accrued">What it accrued.</param>
/// <param name="Settled">What the credit settled of it, oldest months first.</param>
/// <param name="Paused">The mode of the pause covering it, or <c>null</c>.</param>
public sealed record PlanPeriod(YearMonth Month, Money Accrued, Money Settled, PauseMode? Paused = null)
{
    /// <summary>What is still unpaid of this month.</summary>
    public Money Unpaid => Accrued - Settled;
}
