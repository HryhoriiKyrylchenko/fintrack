using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Time;
using static FinTrack.Budget.Domain.Tests.State.PlanJournal;

namespace FinTrack.Budget.Domain.Tests.State;

/// <summary>
/// Invariant 6 (plan 1b B8): <see cref="BudgetState.FirstPastPeriodChange"/> finds the first month before the current one
/// in which a plan accrues differently after a change, a new plan never counts, and a plan that disappears accrues
/// nothing. Today is 15 March 2026: February and earlier are past.
/// </summary>
public class PastPeriodTests
{
    private readonly PlanJournal journal = new(new DateOnly(2026, 3, 15));

    [Fact]
    public void AVersionFromLastMonth_ChangesLastMonth()
    {
        var plan = journal.Create(journal.Cushion, 500m, Month(2025, 12), day: -100);

        FirstChange([plan], journal.Change(plan, 600m, Month(2026, 2))).ShouldBe(Month(2026, 2));
    }

    [Fact]
    public void AVersionFromTheCurrentMonthOrLater_ChangesNoPastMonth()
    {
        var plan = journal.Create(journal.Cushion, 500m, Month(2025, 12), day: -100);

        FirstChange([plan], journal.Change(plan, 600m, Month(2026, 3))).ShouldBeNull();
        FirstChange([plan], journal.Change(plan, 600m, Month(2026, 9))).ShouldBeNull();
    }

    [Fact]
    public void AVersionFromAPastMonth_WithTheSameAmount_ChangesNothing()
    {
        var plan = journal.Create(journal.Cushion, 500m, Month(2025, 12), day: -100);

        FirstChange([plan], journal.Change(plan, 500m, Month(2026, 1))).ShouldBeNull();
    }

    [Fact]
    public void AVersionFromAPastMonth_BeforeAPlanStarts_ChangesNothing()
    {
        var plan = journal.Create(journal.Cushion, 500m, Month(2026, 5));

        FirstChange([plan], journal.Change(plan, 600m, Month(2026, 1))).ShouldBeNull();
    }

    [Fact]
    public void AnEnd_ChangesTheMonthsItRemovesOrAdds()
    {
        var plan = journal.Create(journal.Cushion, 500m, Month(2025, 11), day: -120);
        var ended = journal.End(plan, Month(2025, 12), day: -80);

        // Ending it as of last month removes only the current month; two months ago removes February too.
        FirstChange([plan], journal.End(plan, Month(2026, 2))).ShouldBeNull();
        FirstChange([plan], journal.End(plan, Month(2026, 1))).ShouldBe(Month(2026, 2));
        FirstChange([plan], journal.End(plan, Month(2025, 10))).ShouldBe(Month(2025, 11));

        // Moving the end of an ended plan later revives past months, unless only the current month is added.
        FirstChange([plan, ended], journal.End(plan, null)).ShouldBe(Month(2026, 1));
        FirstChange([plan, ended], journal.End(plan, Month(2026, 5))).ShouldBe(Month(2026, 1));
    }

    [Fact]
    public void ExtendingAPlanThatEndedLastMonth_ChangesNoPastMonth()
    {
        var plan = journal.Create(journal.Cushion, 500m, Month(2026, 1), Month(2026, 2), day: -70);

        FirstChange([plan], journal.End(plan, Month(2026, 12))).ShouldBeNull();
    }

    [Fact]
    public void ANewPlan_StartingInThePast_ChangesNothing()
    {
        var other = journal.Create(journal.Holiday, 100m, Month(2026, 1), day: -70);

        FirstChange([other], journal.Create(journal.Cushion, 500m, Month(2025, 6))).ShouldBeNull();
    }

    [Fact]
    public void ReversingAPlan_ChangesItsFirstPastMonth_UnlessItHasNone()
    {
        var past = journal.Create(journal.Cushion, 500m, Month(2025, 12), day: -100);
        FirstChange([past], journal.Reverse(past)).ShouldBe(Month(2025, 12));

        var current = journal.Create(journal.Cushion, 500m, Month(2026, 3));
        FirstChange([current], journal.Reverse(current)).ShouldBeNull();
    }

    [Fact]
    public void ReversingAPastVersion_ChangesItsMonth()
    {
        var plan = journal.Create(journal.Cushion, 500m, Month(2025, 12), day: -100);
        var change = journal.Change(plan, 700m, Month(2026, 1), day: -70);

        FirstChange([plan, change], journal.Reverse(change)).ShouldBe(Month(2026, 1));
    }

    [Fact]
    public void Contributions_NeverChangeAccrual()
    {
        var plan = journal.Create(journal.Cushion, 500m, Month(2025, 12), day: -100);

        FirstChange([plan], journal.Pay(journal.Cushion, 900m, plan, day: -60)).ShouldBeNull();
    }

    [Fact]
    public void TheEarliestChangedMonth_OfAllPlans_IsReported()
    {
        var first = journal.Create(journal.Cushion, 500m, Month(2025, 12), day: -100);
        var second = journal.Create(journal.Holiday, 100m, Month(2025, 10), day: -150);
        var before = journal.Replay(first, second);

        var after = journal.Replay(first, second, journal.Change(first, 1m, Month(2026, 2)), journal.Change(second, 2m, Month(2026, 1)));

        before.FirstPastPeriodChange(after).ShouldBe(Month(2026, 1));
    }

    private YearMonth? FirstChange(JournalEntry[] existing, JournalEntry added) =>
        journal.Replay(existing).FirstPastPeriodChange(journal.Replay([.. existing, added]));
}
