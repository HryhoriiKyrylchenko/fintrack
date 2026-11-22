using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.Plans;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Monetary;
using static FinTrack.Budget.Domain.Tests.State.PlanJournal;

namespace FinTrack.Budget.Domain.Tests.State;

/// <summary>
/// Contribution plans in the state builder and the period engine (ARCHITECTURE 4.5, plan 1b B7, B9): accrual per month
/// with versions and an end, credit settling the oldest months first, <c>DueNow</c>, <c>Shortfall</c>, <c>Prepaid</c>,
/// status, linked deposits and transfers, reversals. Today is 15 March 2026 unless a test says otherwise.
/// </summary>
public class PlanStateTests
{
    private readonly PlanJournal journal = new(new DateOnly(2026, 3, 15));

    [Fact]
    public void TheExampleOf45_500AMonth_300Paid_Leaves700Due_200OfItCarriedOver()
    {
        var plan = journal.Create(journal.Cushion, 500m, Month(2026, 2), day: -40);
        var state = journal.Replay(plan, journal.Pay(journal.Cushion, 300m, plan, day: -35));

        var standing = state.StandingOf(state.FindPlan(plan.PlanId)!);

        standing.CurrentMonth.ShouldBe(Month(2026, 3));
        standing.Accrued.ShouldBe(Pln(1000m));
        standing.Credit.ShouldBe(Pln(300m));
        standing.DueNow.ShouldBe(Pln(700m));
        standing.Shortfall.ShouldBe(Pln(200m));
        standing.Prepaid.ShouldBe(Pln(0m));
        standing.Status.ShouldBe(PlanStatus.Active);
        standing.IsActive.ShouldBeTrue();
        standing.Periods.Select(period => (period.Month, period.Accrued, period.Settled, period.Unpaid)).ShouldBe(
        [
            (Month(2026, 2), Pln(500m), Pln(300m), Pln(200m)),
            (Month(2026, 3), Pln(500m), Pln(0m), Pln(500m)),
        ]);
    }

    [Fact]
    public void TheCurrentMonth_IsDueDuringTheMonth_ButIsNoShortfall()
    {
        var plan = journal.Create(journal.Cushion, 200m, Month(2026, 3));

        var standing = journal.Replay(plan).StandingOf(journal.Replay(plan).Plans.ShouldHaveSingleItem());

        standing.DueNow.ShouldBe(Pln(200m));
        standing.Shortfall.ShouldBe(Pln(0m));
    }

    [Fact]
    public void Credit_SettlesTheOldestMonthFirst_WhateverTheDatesOfTheContributions()
    {
        var plan = journal.Create(journal.Cushion, 100m, Month(2026, 1), day: -70);
        var state = journal.Replay(plan, journal.Pay(journal.Cushion, 150m, plan, day: 0));

        var standing = state.StandingOf(state.Plans.ShouldHaveSingleItem());

        standing.Periods.Select(period => period.Settled).ShouldBe([Pln(100m), Pln(50m), Pln(0m)]);
        standing.DueNow.ShouldBe(Pln(150m));
        standing.Shortfall.ShouldBe(Pln(50m));
    }

    [Fact]
    public void Overpaying_IsAPrepayment_ThatSettlesLaterMonthsAsTheyAccrue()
    {
        var march = new PlanJournal(new DateOnly(2026, 3, 15));
        var plan = march.Create(march.Cushion, 500m, Month(2026, 3));
        var paid = march.Pay(march.Cushion, 1500m, plan);

        var now = march.Replay(plan, paid);
        var standing = now.StandingOf(now.Plans.Single());
        standing.Prepaid.ShouldBe(Pln(1000m));
        standing.DueNow.ShouldBe(Pln(0m));

        // Two months later the prepayment has settled April and May.
        var later = ReplayOn(new DateOnly(2026, 5, 20), march, plan, paid);
        var then = later.StandingOf(later.Plans.Single());
        then.Prepaid.ShouldBe(Pln(0m));
        then.DueNow.ShouldBe(Pln(0m));
        then.Periods.ShouldAllBe(period => period.Unpaid.Amount == 0m);

        var june = ReplayOn(new DateOnly(2026, 6, 1), march, plan, paid);
        june.StandingOf(june.Plans.Single()).DueNow.ShouldBe(Pln(500m));
    }

    [Fact]
    public void AVersion_ChangesTheAmountFromItsMonth_AndTheLaterOfTwoFromTheSameMonthWins()
    {
        var plan = journal.Create(journal.Cushion, 500m, Month(2026, 1), day: -70);
        var state = journal.Replay(
            plan,
            journal.Change(plan, 800m, Month(2026, 3)),
            journal.Change(plan, 900m, Month(2026, 3)),
            journal.Change(plan, 50m, Month(2026, 6)));

        var found = state.Plans.ShouldHaveSingleItem();

        PlanSchedule.Accrual(found, Month(2026, 2)).ShouldBe(Pln(500m));
        PlanSchedule.Accrual(found, Month(2026, 3)).ShouldBe(Pln(900m));
        PlanSchedule.Accrual(found, Month(2026, 5)).ShouldBe(Pln(900m));
        PlanSchedule.Accrual(found, Month(2026, 6)).ShouldBe(Pln(50m));
        state.StandingOf(found).Accrued.ShouldBe(Pln(1900m));
    }

    [Fact]
    public void NothingAccrues_BeforeTheStart_OrAfterTheEnd()
    {
        var plan = journal.Create(journal.Cushion, 500m, Month(2026, 2), Month(2026, 4), day: -40);
        var found = journal.Replay(plan).Plans.Single();

        PlanSchedule.Accrual(found, Month(2026, 1)).ShouldBe(Pln(0m));
        PlanSchedule.Accrual(found, Month(2026, 2)).ShouldBe(Pln(500m));
        PlanSchedule.Accrual(found, Month(2026, 4)).ShouldBe(Pln(500m));
        PlanSchedule.Accrual(found, Month(2026, 5)).ShouldBe(Pln(0m));
    }

    [Fact]
    public void AnEndedPlan_KeepsItsShortfall_AndStaysActiveUntilItIsPaid()
    {
        var plan = journal.Create(journal.Cushion, 500m, Month(2025, 12), day: -100);
        var ended = journal.End(plan, Month(2026, 1), day: -40);

        var state = journal.Replay(plan, ended, journal.Pay(journal.Cushion, 600m, plan, day: -40));
        var standing = state.StandingOf(state.Plans.Single());

        standing.Status.ShouldBe(PlanStatus.Ended);
        standing.Periods.Select(period => period.Month).ShouldBe([Month(2025, 12), Month(2026, 1)]);
        standing.DueNow.ShouldBe(Pln(400m));
        standing.Shortfall.ShouldBe(Pln(400m));
        standing.IsActive.ShouldBeTrue();

        var paid = journal.Replay(plan, ended, journal.Pay(journal.Cushion, 1000m, plan, day: -40));
        var settled = paid.StandingOf(paid.Plans.Single());
        settled.Status.ShouldBe(PlanStatus.Ended);
        settled.DueNow.ShouldBe(Pln(0m));
        settled.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void APlanEndingThisMonth_IsActive_AndTheLatestEndWins()
    {
        var plan = journal.Create(journal.Cushion, 100m, Month(2026, 3), Month(2026, 3));
        var standing = journal.Replay(plan).StandingOf(journal.Replay(plan).Plans.Single());
        standing.Status.ShouldBe(PlanStatus.Active);

        var state = journal.Replay(plan, journal.End(plan, null), journal.End(plan, Month(2026, 8)));
        state.Plans.Single().ValidTo.ShouldBe(Month(2026, 8));

        var open = journal.Replay(plan, journal.End(plan, Month(2026, 8)), journal.End(plan, null));
        open.Plans.Single().ValidTo.ShouldBeNull();
    }

    [Fact]
    public void APlanStartingLater_HasNoPeriodsYet_AndIsActive_AndContributionsArePrepaid()
    {
        var plan = journal.Create(journal.Cushion, 100m, Month(2026, 5));
        var state = journal.Replay(plan, journal.Pay(journal.Cushion, 40m, plan));

        var standing = state.StandingOf(state.Plans.Single());

        standing.Periods.ShouldBeEmpty();
        standing.Status.ShouldBe(PlanStatus.Active);
        standing.IsActive.ShouldBeTrue();
        standing.DueNow.ShouldBe(Pln(0m));
        standing.Prepaid.ShouldBe(Pln(40m));
    }

    [Fact]
    public void APlan_AcrossAYearBoundary_AccruesEveryMonth()
    {
        var january = new PlanJournal(new DateOnly(2026, 1, 15));
        var plan = january.Create(january.Cushion, 100m, Month(2025, 11), day: -60);

        var state = january.Replay(plan);
        var standing = state.StandingOf(state.Plans.Single());

        standing.Periods.Select(period => period.Month).ShouldBe([Month(2025, 11), Month(2025, 12), Month(2026, 1)]);
        standing.Shortfall.ShouldBe(Pln(200m));
        standing.DueNow.ShouldBe(Pln(300m));
    }

    [Fact]
    public void LinkedDepositsAndTransfersIn_AreCredit_OneTimeDepositsAreNot()
    {
        var plan = journal.Create(journal.Holiday, 500m, Month(2026, 3));
        var state = journal.Replay(
            plan,
            journal.Pay(journal.Cushion, 1000m, null, day: -1),
            journal.Move(journal.Cushion, journal.Holiday, 200m, plan),
            journal.Pay(journal.Holiday, 50m, plan),
            journal.Pay(journal.Holiday, 75m, null));

        var found = state.Plans.Single();

        found.Contributed.ShouldBe(Pln(250m));
        state.StandingOf(found).DueNow.ShouldBe(Pln(250m));
        state.BalanceOf(journal.Holiday.BucketId).NetContributed.ShouldBe(Pln(325m));
        state.BalanceOf(journal.Holiday.BucketId).Deposited.ShouldBe(Pln(125m));
    }

    [Fact]
    public void AChangeDatedBeforeItsPlan_StillBelongsToIt()
    {
        var plan = journal.Create(journal.Cushion, 500m, Month(2026, 3), day: 0);
        var change = journal.Change(plan, 300m, Month(2026, 4), day: -5);
        var deposit = journal.Pay(journal.Cushion, 100m, plan, day: -6);

        var found = journal.Replay(plan, change, deposit).Plans.Single();

        found.Versions.Count.ShouldBe(2);
        found.Contributed.ShouldBe(Pln(100m));
    }

    [Fact]
    public void ReversedEntries_AreLeftOut()
    {
        var plan = journal.Create(journal.Cushion, 500m, Month(2026, 3));
        var change = journal.Change(plan, 300m, Month(2026, 3));
        var end = journal.End(plan, Month(2026, 4));
        var deposit = journal.Pay(journal.Cushion, 100m, plan);

        var state = journal.Replay(plan, change, end, deposit, journal.Reverse(change), journal.Reverse(end), journal.Reverse(deposit));
        var found = state.Plans.Single();
        found.Versions.ShouldHaveSingleItem();
        found.ValidTo.ShouldBeNull();
        found.Contributed.ShouldBe(Pln(0m));

        journal.Replay(plan, journal.Reverse(plan)).Plans.ShouldBeEmpty();
        journal.Replay(plan, journal.Reverse(plan)).FindPlan(plan.PlanId).ShouldBeNull();
    }

    [Fact]
    public void APlan_HoldsItsDefinition()
    {
        var plan = journal.Create(journal.Holiday, 120m, Month(2026, 2), Month(2026, 12), day: -30);

        var found = journal.Replay(plan).FindPlan(plan.PlanId)!;

        found.PlanId.ShouldBe(plan.PlanId);
        found.BucketId.ShouldBe(journal.Holiday.BucketId);
        found.ValidFrom.ShouldBe(Month(2026, 2));
        found.ValidTo.ShouldBe(Month(2026, 12));
        found.CreatedOn.ShouldBe(new DateOnly(2026, 2, 13));
        found.Versions.ShouldBe([new PlanVersion(Month(2026, 2), Pln(120m))]);
        found.Contributed.ShouldBe(Pln(0m));
        found.Currency.ShouldBe(Currency.Pln);
    }

    [Fact]
    public void PlanEntries_MoveNoMoney()
    {
        var plan = journal.Create(journal.Cushion, 500m, Month(2026, 1), day: -70);

        var state = journal.Replay(journal.Pay(journal.Cushion, 80m, null, day: -80), plan, journal.Change(plan, 10m, Month(2026, 4)), journal.End(plan, Month(2026, 6)));

        state.BalanceOf(journal.Cushion.BucketId).NetContributed.ShouldBe(Pln(80m));
        state.BalanceOf(journal.Cushion.BucketId).Deposited.ShouldBe(Pln(80m));
        state.FirstOverdraft.ShouldBeNull();
    }

    private static BudgetState ReplayOn(DateOnly today, PlanJournal source, params JournalEntry[] entries) =>
        BudgetState.Replay([source.Cushion, source.Holiday], entries, today);
}
