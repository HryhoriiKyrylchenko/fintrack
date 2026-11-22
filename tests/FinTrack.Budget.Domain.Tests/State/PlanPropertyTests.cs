using CsCheck;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.Plans;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Time;

namespace FinTrack.Budget.Domain.Tests.State;

/// <summary>
/// Property-based tests of the period engine (plan 1b B7, B8): what a plan owes equals an independent month-by-month sum,
/// and the past-month comparison finds exactly the first month whose accrual a change alters (invariant 6), checked
/// against an independent accrual built from the entries themselves.
/// </summary>
public class PlanPropertyTests
{
    private static readonly YearMonth Current = new(2026, 3);

    // A start up to 30 months back or 3 ahead, an end 0 to 36 months after the start or none, up to five versions from
    // 0 to 40 months after the start, contributions, and a current month near the start.
    private static readonly Gen<Plan> Plans =
        Gen.Select(
            Gen.Int[-30, 3],
            Gen.OneOf(Gen.Int[0, 36].Select(months => (int?)months), Gen.Const((int?)null)),
            Gen.Select(Gen.Int[0, 40], Gen.Long[1, 1000]).Array[0, 5],
            Gen.Long[0, 20_000])
        .Select((start, end, versions, contributed) =>
        {
            var from = Current.AddMonths(start);
            return new Plan(
                PlanId.From(Guid.CreateVersion7()),
                BucketId.From(Guid.CreateVersion7()),
                from,
                end is { } months ? from.AddMonths(months) : null,
                new DateOnly(2026, 1, 1),
                [new PlanVersion(from, Pln(100m)), .. versions.Select(version => new PlanVersion(from.AddMonths(version.Item1), Pln(version.Item2)))],
                Pln(contributed));
        });

    [Fact]
    public void Dues_EqualAnIndependentMonthByMonthSum() =>
        Gen.Select(Plans, Gen.Int[-6, 40]).Sample((plan, offset) =>
        {
            var current = plan.ValidFrom.AddMonths(offset);
            var standing = PlanSchedule.Standing(plan, current);

            decimal accrued = 0m, before = 0m;
            var months = new List<YearMonth>();
            for (var month = plan.ValidFrom; month <= current && !(month > plan.ValidTo); month = month.Next())
            {
                var amount = IndependentAmount(plan.Versions, month);
                accrued += amount;
                before += month < current ? amount : 0m;
                months.Add(month);
            }

            var credit = plan.Contributed.Amount;
            return standing.Accrued.Amount == accrued
                && standing.DueNow.Amount == Math.Max(0m, accrued - credit)
                && standing.Shortfall.Amount == Math.Max(0m, before - credit)
                && standing.Prepaid.Amount == Math.Max(0m, credit - accrued)
                && standing.Periods.Select(period => period.Month).SequenceEqual(months)
                && standing.Periods.Sum(period => period.Settled.Amount) == Math.Min(credit, accrued)
                && standing.Status == (plan.ValidTo < current ? PlanStatus.Ended : PlanStatus.Active);
        });

    [Fact]
    public void Credit_SettlesOldestFirst_NoLaterMonthBeforeAnEarlierOneIsPaid() =>
        Gen.Select(Plans, Gen.Int[0, 40]).Sample((plan, offset) =>
        {
            var periods = PlanSchedule.Standing(plan, plan.ValidFrom.AddMonths(offset)).Periods;
            return periods.Zip(periods.Skip(1)).All(pair => pair.Second.Settled.Amount == 0m || pair.First.Unpaid.Amount == 0m);
        });

    private enum Change
    {
        Version,
        End,
        ReverseOne,
        NewPlan,
    }

    // An existing history of one plan (start, end, versions), then one change.
    private static readonly Gen<(int Start, int? End, (int From, long Amount)[] Versions, Change Change, int Month, long Amount, int Pick)> Histories =
        Gen.Select(
            Gen.Int[-12, 2],
            Gen.OneOf(Gen.Int[0, 18].Select(months => (int?)months), Gen.Const((int?)null)),
            Gen.Select(Gen.Int[-12, 6], Gen.Long[1, 5]).Array[0, 4],
            Gen.Enum<Change>(),
            Gen.Int[-14, 6],
            Gen.Long[1, 5],
            Gen.Int[0, 100]);

    [Fact]
    public void ThePastMonthComparison_FindsExactlyTheFirstChangedMonth() =>
        Histories.Sample(history =>
        {
            var journal = new PlanJournal(new DateOnly(2026, 3, 15));
            var start = Current.AddMonths(history.Start);
            var plan = journal.Create(journal.Cushion, 3m, start, history.End is { } end ? start.AddMonths(end) : null, day: -300);
            var existing = new List<JournalEntry> { plan };
            existing.AddRange(history.Versions.Select(version => (JournalEntry)journal.Change(plan, version.Amount, Current.AddMonths(version.From), day: -200)));
            if (history.End is not null && history.Pick % 3 == 0)
            {
                existing.Add(journal.End(plan, Current.AddMonths(history.Month), day: -100));
            }

            JournalEntry added = history.Change switch
            {
                Change.Version => journal.Change(plan, history.Amount, Current.AddMonths(history.Month)),
                Change.End => journal.End(plan, history.Pick % 4 == 0 ? null : Current.AddMonths(history.Month)),
                Change.ReverseOne => journal.Reverse(existing[history.Pick % existing.Count]),
                _ => journal.Create(journal.Holiday, history.Amount, Current.AddMonths(history.Month)),
            };

            var found = journal.Replay([.. existing]).FirstPastPeriodChange(journal.Replay([.. existing, added]));

            return found == IndependentFirstChange(plan, existing, [.. existing, added]);
        });

    // The amount of the version in force: the latest "from" at or before the month, the later of equal ones in list order.
    private static decimal IndependentAmount(IReadOnlyList<PlanVersion> versions, YearMonth month)
    {
        decimal amount = 0m;
        YearMonth? best = null;
        foreach (var version in versions)
        {
            if (version.From <= month && (best is null || version.From >= best))
            {
                best = version.From;
                amount = version.Amount.Amount;
            }
        }

        return amount;
    }

    // Accrual of the plan in a month, rebuilt from the effective entries in the order they were built (their replay order).
    private static decimal IndependentAccrual(PlanCreated plan, IReadOnlyList<JournalEntry> journal, YearMonth month)
    {
        var reversed = journal.OfType<Reversal>().Select(reversal => reversal.ReversedEntryId.Value).ToHashSet();
        var effective = journal.Where(entry => entry is not Reversal && !reversed.Contains(entry.Id)).ToList();
        if (!effective.Contains(plan))
        {
            return 0m;
        }

        var versions = new List<PlanVersion> { new(plan.ValidFrom, plan.Amount) };
        var validTo = plan.ValidTo;
        foreach (var entry in effective)
        {
            if (entry is PlanAmountChanged changed && changed.PlanId == plan.PlanId)
            {
                versions.Add(new PlanVersion(changed.From, changed.Amount));
            }
            else if (entry is PlanEndChanged ended && ended.PlanId == plan.PlanId)
            {
                validTo = ended.ValidTo;
            }
        }

        return month < plan.ValidFrom || (validTo is { } last && month > last) ? 0m : IndependentAmount(versions, month);
    }

    private static YearMonth? IndependentFirstChange(PlanCreated plan, IReadOnlyList<JournalEntry> before, IReadOnlyList<JournalEntry> after)
    {
        for (var month = plan.ValidFrom; month < Current; month = month.Next())
        {
            if (IndependentAccrual(plan, before, month) != IndependentAccrual(plan, after, month))
            {
                return month;
            }
        }

        return null;
    }

    private static Money Pln(decimal amount) => Money.From(amount, Currency.Pln);
}
