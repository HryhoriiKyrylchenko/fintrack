using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.Plans;
using FinTrack.Budget.Domain.State;
using static FinTrack.Budget.Domain.Tests.State.PlanJournal;

namespace FinTrack.Budget.Domain.Tests.State;

/// <summary>
/// Pauses and waivers in the state builder and the period engine (ARCHITECTURE 4.5, plan 1b B10, B11): paused months accrue
/// nothing, a shift pause moves the end and a skip pause keeps it, a pause's latest change wins, the status, and waivers that settle
/// only earlier months and never create a prepayment. Today is 15 March 2026.
/// </summary>
public class PauseAndWaiverStateTests
{
    private readonly PlanJournal journal = new(new DateOnly(2026, 3, 15));

    [Fact]
    public void PausedMonths_AccrueNothing_InBothModes()
    {
        var plan = journal.Create(journal.Cushion, 100m, Month(2026, 1), day: -70);

        foreach (var mode in new[] { PauseMode.Shift, PauseMode.Skip })
        {
            var found = journal.Replay(plan, journal.Pause(plan, Month(2026, 2), Month(2026, 3), mode)).Plans.Single();

            PlanSchedule.Accrual(found, Month(2026, 1)).ShouldBe(Pln(100m));
            PlanSchedule.Accrual(found, Month(2026, 2)).ShouldBe(Pln(0m));
            PlanSchedule.Accrual(found, Month(2026, 3)).ShouldBe(Pln(0m));
            PlanSchedule.Accrual(found, Month(2026, 4)).ShouldBe(Pln(100m));
        }
    }

    [Fact]
    public void AShiftPause_MovesTheEndByThePausedMonths_AndASkipPauseKeepsIt()
    {
        var plan = journal.Create(journal.Cushion, 100m, Month(2026, 1), Month(2026, 6), day: -70);

        var shifted = journal.Replay(plan, journal.Pause(plan, Month(2026, 3), Month(2026, 4), PauseMode.Shift)).Plans.Single();
        var skipped = journal.Replay(plan, journal.Pause(plan, Month(2026, 3), Month(2026, 4), PauseMode.Skip)).Plans.Single();

        PlanSchedule.EffectiveEnd(shifted).ShouldBe(Month(2026, 8));
        PlanSchedule.Accrual(shifted, Month(2026, 8)).ShouldBe(Pln(100m));
        PlanSchedule.Accrual(shifted, Month(2026, 9)).ShouldBe(Pln(0m));
        PlanSchedule.EffectiveEnd(skipped).ShouldBe(Month(2026, 6));
        PlanSchedule.Accrual(skipped, Month(2026, 7)).ShouldBe(Pln(0m));
    }

    [Fact]
    public void AShiftPauseWithoutAnEnd_LeavesTheEndUnknown_AndOneOutsideThePlansMonthsMovesNothing()
    {
        var plan = journal.Create(journal.Cushion, 100m, Month(2026, 1), Month(2026, 6), day: -70);

        PlanSchedule.EffectiveEnd(journal.Replay(plan, journal.Pause(plan, Month(2026, 4), null, PauseMode.Shift)).Plans.Single()).ShouldBeNull();
        PlanSchedule.EffectiveEnd(journal.Replay(plan, journal.Pause(plan, Month(2026, 7), Month(2026, 9), PauseMode.Shift)).Plans.Single()).ShouldBe(Month(2026, 6));
        PlanSchedule.EffectiveEnd(journal.Replay(plan, journal.Pause(plan, Month(2025, 10), Month(2025, 12), PauseMode.Shift)).Plans.Single()).ShouldBe(Month(2026, 6));

        // A plan without an end has none to move.
        var open = journal.Create(journal.Holiday, 100m, Month(2026, 1), day: -70);
        PlanSchedule.EffectiveEnd(journal.Replay(open, journal.Pause(open, Month(2026, 2), Month(2026, 2), PauseMode.Shift)).Plans.Single()).ShouldBeNull();
    }

    [Fact]
    public void SeveralShiftPauses_AddUp()
    {
        var plan = journal.Create(journal.Cushion, 100m, Month(2026, 1), Month(2026, 3), day: -70);

        var found = journal.Replay(
            plan,
            journal.Pause(plan, Month(2026, 2), Month(2026, 2), PauseMode.Shift),
            journal.Pause(plan, Month(2026, 4), Month(2026, 5), PauseMode.Shift),
            journal.Pause(plan, Month(2026, 7), Month(2026, 7), PauseMode.Skip)).Plans.Single();

        // Jan, Mar and Jun are the three months that accrue.
        PlanSchedule.EffectiveEnd(found).ShouldBe(Month(2026, 6));
        Enumerable.Range(0, 8).Select(offset => PlanSchedule.Accrual(found, Month(2026, 1).AddMonths(offset)).Amount)
            .ShouldBe([100m, 0m, 100m, 0m, 0m, 100m, 0m, 0m]);
    }

    [Fact]
    public void APausesLatestChange_Wins_AndResumingSetsItsLastMonth()
    {
        var plan = journal.Create(journal.Cushion, 100m, Month(2026, 1), day: -70);
        var pause = journal.Pause(plan, Month(2026, 3), null, PauseMode.Shift);

        var resumed = journal.Replay(plan, pause, journal.ChangePause(pause, Month(2026, 3), Month(2026, 4), PauseMode.Skip)).Plans.Single();
        resumed.Pauses.ShouldBe([new PlanPause(pause.PauseId, Month(2026, 3), Month(2026, 4), PauseMode.Skip)]);
        PlanSchedule.Accrual(resumed, Month(2026, 5)).ShouldBe(Pln(100m));

        var twice = journal.Replay(plan, pause, journal.ChangePause(pause, Month(2026, 3), Month(2026, 4), PauseMode.Skip), journal.ChangePause(pause, Month(2026, 4), Month(2026, 6), PauseMode.Shift)).Plans.Single();
        twice.Pauses.Single().ShouldBe(new PlanPause(pause.PauseId, Month(2026, 4), Month(2026, 6), PauseMode.Shift));

        var reversed = journal.Replay(plan, pause, journal.Reverse(pause)).Plans.Single();
        reversed.Pauses.ShouldBeEmpty();
    }

    [Fact]
    public void AChangeDatedBeforeItsPause_StillBelongsToIt()
    {
        var plan = journal.Create(journal.Cushion, 100m, Month(2026, 1), day: -70);
        var pause = journal.Pause(plan, Month(2026, 3), null, PauseMode.Skip, day: 0);
        var change = journal.ChangePause(pause, Month(2026, 3), Month(2026, 3), PauseMode.Skip, day: -3);

        journal.Replay(plan, pause, change).Plans.Single().Pauses.Single().To.ShouldBe(Month(2026, 3));
    }

    [Fact]
    public void AStatus_IsPausedWhileAPauseCoversTheCurrentMonth_AndEndedAfterTheEffectiveEnd()
    {
        var plan = journal.Create(journal.Cushion, 100m, Month(2026, 1), Month(2026, 2), day: -70);
        var paused = journal.Create(journal.Holiday, 100m, Month(2026, 1), day: -70);

        var state = journal.Replay(plan, paused, journal.Pause(paused, Month(2026, 3), null, PauseMode.Skip));

        state.StandingOf(state.FindPlan(paused.PlanId)!).Status.ShouldBe(PlanStatus.Paused);
        state.StandingOf(state.FindPlan(paused.PlanId)!).IsActive.ShouldBeTrue();
        state.StandingOf(state.FindPlan(plan.PlanId)!).Status.ShouldBe(PlanStatus.Ended);

        // A shift pause in February moves the end to March, so the plan has not ended.
        var shifted = journal.Replay(plan, journal.Pause(plan, Month(2026, 2), Month(2026, 2), PauseMode.Shift));
        var standing = shifted.StandingOf(shifted.Plans.Single());
        standing.Status.ShouldBe(PlanStatus.Active);
        standing.EffectiveEnd.ShouldBe(Month(2026, 3));
        standing.Periods.Select(period => (period.Month, period.Accrued.Amount, period.Paused))
            .ShouldBe([(Month(2026, 1), 100m, (PauseMode?)null), (Month(2026, 2), 0m, PauseMode.Shift), (Month(2026, 3), 100m, null)]);
    }

    [Fact]
    public void AWaiver_SettlesTheShortfall_ButNeverTheCurrentMonth()
    {
        var plan = journal.Create(journal.Cushion, 500m, Month(2026, 1), day: -70);
        var state = journal.Replay(plan, journal.Pay(journal.Cushion, 300m, plan, day: -60), journal.Waive(plan, 2000m));

        var standing = state.StandingOf(state.Plans.Single());

        standing.Waived.ShouldBe(Pln(700m));
        standing.Credit.ShouldBe(Pln(1000m));
        standing.Shortfall.ShouldBe(Pln(0m));
        standing.DueNow.ShouldBe(Pln(500m));
        standing.Prepaid.ShouldBe(Pln(0m));
    }

    [Fact]
    public void ShortfallAt_IsWhatEarlierMonthsLack_AfterContributionsAndWaivers()
    {
        var plan = journal.Create(journal.Cushion, 500m, Month(2026, 1), day: -70);
        var found = journal.Replay(plan, journal.Pay(journal.Cushion, 300m, plan, day: -60), journal.Waive(plan, 200m)).Plans.Single();

        PlanSchedule.ShortfallAt(found, Month(2026, 3)).ShouldBe(Pln(500m));
        PlanSchedule.ShortfallAt(found, Month(2026, 2)).ShouldBe(Pln(0m));
        PlanSchedule.ShortfallAt(found, Month(2026, 1)).ShouldBe(Pln(0m));
    }

    [Fact]
    public void ABackdatedContribution_VoidsTheWaiverPartNoLongerNeeded_SoNoPrepaymentArises()
    {
        var plan = journal.Create(journal.Cushion, 500m, Month(2026, 1), day: -70);
        var waiver = journal.Waive(plan, 1000m, day: -10);

        var state = journal.Replay(plan, waiver, journal.Pay(journal.Cushion, 800m, plan, day: -50));
        var standing = state.StandingOf(state.Plans.Single());

        standing.Waived.ShouldBe(Pln(200m));
        standing.Credit.ShouldBe(Pln(1000m));
        standing.Prepaid.ShouldBe(Pln(0m));
        standing.DueNow.ShouldBe(Pln(500m));
    }

    [Fact]
    public void AWaiverNeverCoversItsOwnMonthLater_WhenThatMonthBecomesPast()
    {
        // A waiver in March of February's shortfall; in May, March and April are unpaid and the waiver does not reach them.
        var march = new PlanJournal(new DateOnly(2026, 3, 15));
        var plan = march.Create(march.Cushion, 100m, Month(2026, 2), day: -40);
        var waiver = march.Waive(plan, 500m);

        var may = ReplayOn(new DateOnly(2026, 5, 10), march, plan, waiver);
        var standing = may.StandingOf(may.Plans.Single());

        standing.Waived.ShouldBe(Pln(100m));
        standing.Shortfall.ShouldBe(Pln(200m));
        standing.DueNow.ShouldBe(Pln(300m));
    }

    [Fact]
    public void ReversingAWaiver_BringsTheShortfallBack()
    {
        var plan = journal.Create(journal.Cushion, 500m, Month(2026, 1), day: -70);
        var waiver = journal.Waive(plan, 400m);

        var state = journal.Replay(plan, waiver, journal.Reverse(waiver));
        var standing = state.StandingOf(state.Plans.Single());

        standing.Waived.ShouldBe(Pln(0m));
        standing.Shortfall.ShouldBe(Pln(1000m));
    }

    [Fact]
    public void PausesAndWaivers_MoveNoMoney()
    {
        var plan = journal.Create(journal.Cushion, 500m, Month(2026, 1), day: -70);

        var state = journal.Replay(journal.Pay(journal.Cushion, 80m, null, day: -80), plan, journal.Pause(plan, Month(2026, 3), null, PauseMode.Skip), journal.Waive(plan, 100m));

        state.BalanceOf(journal.Cushion.BucketId).NetContributed.ShouldBe(Pln(80m));
        state.BalanceOf(journal.Cushion.BucketId).Deposited.ShouldBe(Pln(80m));
    }

    [Fact]
    public void Pauses_Overlap_WhenTheyShareAMonth()
    {
        var pause = new PlanPause(PauseId.From(Guid.CreateVersion7()), Month(2026, 3), Month(2026, 5), PauseMode.Skip);

        pause.Overlaps(pause with { From = Month(2026, 5), To = Month(2026, 6) }).ShouldBeTrue();
        pause.Overlaps(pause with { From = Month(2026, 1), To = Month(2026, 3) }).ShouldBeTrue();
        pause.Overlaps(pause with { From = Month(2026, 6), To = null }).ShouldBeFalse();
        pause.Overlaps(pause with { From = Month(2026, 1), To = Month(2026, 2) }).ShouldBeFalse();
        (pause with { To = null }).Overlaps(pause with { From = Month(2030, 1), To = null }).ShouldBeTrue();
        pause.Covers(Month(2026, 5)).ShouldBeTrue();
        pause.Covers(Month(2026, 6)).ShouldBeFalse();
        pause.Covers(Month(2026, 2)).ShouldBeFalse();
    }

    private static BudgetState ReplayOn(DateOnly today, PlanJournal source, params JournalEntry[] entries) =>
        BudgetState.Replay([source.Cushion, source.Holiday], entries, today);
}
