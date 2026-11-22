using FinTrack.Budget.Application.Buckets;
using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Application.Plans;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Shared.Kernel.Time;
using FinTrack.Tests.Common;
using ContractPauseMode = FinTrack.Budget.Contracts.V1.Plans.PauseMode;
using DomainPlans = FinTrack.Budget.Domain.Plans;

namespace FinTrack.Budget.Application.Tests.Plans;

/// <summary>
/// <c>PausePlan</c>, <c>ChangePlanPause</c> and <c>WaiveShortfall</c> (plan 1b B10, B11) against in-memory fakes: every rule and its
/// order, invariant 6 with the month, resuming next month, repeats, dependents, reversals, archiving and the journal. Today is
/// 15 January 2026 unless the clock is moved on.
/// </summary>
public class PauseAndWaiverHandlerTests
{
    private static readonly YearMonth January = new(2026, 1);
    private static readonly YearMonth February = new(2026, 2);
    private static readonly YearMonth December = new(2025, 12);
    private static readonly YearMonth November = new(2025, 11);

    private readonly CallLog log = new();
    private readonly FakeLedger ledger;
    private readonly FakeBudgetStore store;
    private readonly FakeClock clock = new();
    private readonly Bucket cushion;
    private readonly Bucket holiday;
    private int stored;

    public PauseAndWaiverHandlerTests()
    {
        ledger = new FakeLedger(log);
        store = new FakeBudgetStore(log) { Settings = StoredSettings.Of(Currency.Pln, "Europe/Warsaw", "pl-PL") };
        cushion = StoredBuckets.Of(Guid.CreateVersion7(), "Cushion", Currency.Pln, 1, FakeClock.DefaultStart, BucketStatus.Active);
        holiday = StoredBuckets.Of(Guid.CreateVersion7(), "Holiday", Currency.Pln, 1, FakeClock.DefaultStart, BucketStatus.Active);
        store.Buckets.AddRange([cushion, holiday]);
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private DateOnly Today => DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

    [Fact]
    public async Task APauseFromTheCurrentMonth_IsWritten_AndThePlanIsPaused()
    {
        var plan = await NewPlan(holiday, 100m, November);
        var id = Guid.CreateVersion7();

        (await Send(new PausePlan(id, plan.Value, January, null, ContractPauseMode.Skip, Today, "Car repair", []))).IsSuccess.ShouldBeTrue();

        var paused = ledger.Commits[^1].Appends.ShouldHaveSingleItem().ShouldBeOfType<PlanPaused>();
        (paused.Id, paused.PlanId, paused.From, paused.To, paused.Mode, paused.Note).ShouldBe((id, plan, January, (YearMonth?)null, DomainPlans.PauseMode.Skip, "Car repair"));
        StoreCommits();
        var standing = Standing(plan);
        standing.Status.ShouldBe(DomainPlans.PlanStatus.Paused);
        standing.Accrued.ShouldBe(Pln(200m));
    }

    [Fact]
    public async Task APauseStartingLastMonth_IsRefusedWithTheMonth_UnlessLastMonthAccruedNothing()
    {
        var plan = await NewPlan(holiday, 100m, November);
        (await Pause(plan, December, null, ContractPauseMode.Shift)).ShouldFailWith(BudgetErrorCodes.PlanPastPeriodChanged, ErrorKind.BusinessRule)
            .Parameters["month"].ShouldBe("2025-12");

        var fresh = await NewPlan(cushion, 100m, January);
        (await Pause(fresh, December, January, ContractPauseMode.Skip)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task APauseIsResumedNextMonth_BySettingItsLastMonth()
    {
        var plan = await NewPlan(holiday, 100m, November);
        var pause = Guid.CreateVersion7();
        (await Send(new PausePlan(pause, plan.Value, January, null, ContractPauseMode.Shift, Today, null, []))).IsSuccess.ShouldBeTrue();
        StoreCommits();

        clock.Advance(TimeSpan.FromDays(31));
        Today.ShouldBe(new DateOnly(2026, 2, 15));
        (await ChangePause(plan, pause, January, January, ContractPauseMode.Shift)).IsSuccess.ShouldBeTrue();
        StoreCommits();

        var standing = Standing(plan);
        standing.Status.ShouldBe(DomainPlans.PlanStatus.Active);
        standing.Periods.Select(period => period.Accrued.Amount).ShouldBe([100m, 100m, 0m, 100m]);

        // Moving the pause to start in February would change January, which has passed.
        (await ChangePause(plan, pause, February, February, ContractPauseMode.Shift)).ShouldFailWith(BudgetErrorCodes.PlanPastPeriodChanged, ErrorKind.BusinessRule)
            .Parameters["month"].ShouldBe("2026-01");
    }

    [Fact]
    public async Task AnOverlappingPause_IsRefused_AndAnAdjacentOneIsNot()
    {
        var plan = await NewPlan(holiday, 100m, January);
        var first = Guid.CreateVersion7();
        (await Send(new PausePlan(first, plan.Value, new YearMonth(2026, 3), new YearMonth(2026, 4), ContractPauseMode.Skip, Today, null, []))).IsSuccess.ShouldBeTrue();
        StoreCommits();

        (await Pause(plan, new YearMonth(2026, 4), null, ContractPauseMode.Skip)).ShouldFailWith(BudgetErrorCodes.PlanPauseOverlaps, ErrorKind.BusinessRule);
        (await Pause(plan, February, new YearMonth(2026, 3), ContractPauseMode.Shift)).ShouldFailWith(BudgetErrorCodes.PlanPauseOverlaps, ErrorKind.BusinessRule);
        (await Pause(plan, new YearMonth(2026, 5), null, ContractPauseMode.Skip)).IsSuccess.ShouldBeTrue();
        StoreCommits();

        // A changed pause is held to the same rule against the others, never against itself.
        (await ChangePause(plan, first, new YearMonth(2026, 3), new YearMonth(2026, 5), ContractPauseMode.Skip)).ShouldFailWith(BudgetErrorCodes.PlanPauseOverlaps, ErrorKind.BusinessRule);
        (await ChangePause(plan, first, new YearMonth(2026, 2), new YearMonth(2026, 4), ContractPauseMode.Skip)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task APause_NeedsAPlanThatHasNotEnded_AndMonthsInOrder_AndAChangeNeedsAPauseOfThePlan()
    {
        var plan = await NewPlan(holiday, 100m, January);
        var ended = await NewPlan(cushion, 100m, November, December);
        (await Pause(plan, new YearMonth(2026, 9), new YearMonth(2026, 8), ContractPauseMode.Skip)).ShouldFailWith(BudgetErrorCodes.PlanMonthsInvalid, ErrorKind.Validation);
        var pause = Guid.CreateVersion7();
        (await Send(new PausePlan(pause, plan.Value, February, null, ContractPauseMode.Skip, Today, null, []))).IsSuccess.ShouldBeTrue();
        StoreCommits();

        (await Pause(DomainPlans.PlanId.From(Guid.CreateVersion7()), January, null, ContractPauseMode.Skip)).ShouldFailWith(BudgetErrorCodes.PlanNotFound, ErrorKind.NotFound);
        (await Pause(ended, January, null, ContractPauseMode.Skip)).ShouldFailWith(BudgetErrorCodes.PlanEnded, ErrorKind.BusinessRule);
        (await ChangePause(plan, Guid.CreateVersion7(), February, null, ContractPauseMode.Skip)).ShouldFailWith(BudgetErrorCodes.PlanPauseNotFound, ErrorKind.NotFound);
        (await ChangePause(ended, pause, February, null, ContractPauseMode.Skip)).ShouldFailWith(BudgetErrorCodes.PlanPauseNotFound, ErrorKind.NotFound);
        (await ChangePause(plan, pause, February, January, ContractPauseMode.Skip)).ShouldFailWith(BudgetErrorCodes.PlanMonthsInvalid, ErrorKind.Validation);
    }

    [Fact]
    public async Task AShiftPause_MovesThePlansEnd()
    {
        var plan = await NewPlan(holiday, 100m, January, new YearMonth(2026, 3));

        (await Pause(plan, February, February, ContractPauseMode.Shift)).IsSuccess.ShouldBeTrue();
        StoreCommits();

        DomainPlans.PlanSchedule.EffectiveEnd(State().FindPlan(plan)!).ShouldBe(new YearMonth(2026, 4));
    }

    [Fact]
    public async Task AWaiver_WritesOffTheShortfall_ButNotTheCurrentMonth_AndItsReversalBringsItBack()
    {
        var plan = await NewPlan(holiday, 500m, November);

        (await Waive(plan, 1000.01m)).ShouldFailWith(BudgetErrorCodes.PlanWaiverExceedsShortfall, ErrorKind.BusinessRule);
        var waiver = Guid.CreateVersion7();
        (await Send(new WaiveShortfall(waiver, plan.Value, Pln(1000m), Today, "Agreed with myself", ["plans"]))).IsSuccess.ShouldBeTrue();
        var written = ledger.Commits[^1].Appends.ShouldHaveSingleItem().ShouldBeOfType<ShortfallWaived>();
        (written.PlanId, written.Amount, written.Note).ShouldBe((plan, Pln(1000m), "Agreed with myself"));
        StoreCommits();

        var standing = Standing(plan);
        (standing.Shortfall.Amount, standing.DueNow.Amount, standing.Waived.Amount).ShouldBe((0m, 500m, 1000m));
        (await Waive(plan, 0.01m)).ShouldFailWith(BudgetErrorCodes.PlanWaiverExceedsShortfall, ErrorKind.BusinessRule);

        (await Reverse(waiver)).IsSuccess.ShouldBeTrue();
        StoreCommits();
        Standing(plan).Shortfall.ShouldBe(Pln(1000m));
    }

    [Fact]
    public async Task AWaiver_NeedsAPlanInItsCurrency_AndAReason()
    {
        var plan = await NewPlan(holiday, 500m, November);

        (await Waive(DomainPlans.PlanId.From(Guid.CreateVersion7()), 1m)).ShouldFailWith(BudgetErrorCodes.PlanNotFound, ErrorKind.NotFound);
        (await Send(new WaiveShortfall(Guid.CreateVersion7(), plan.Value, Money.From(1m, Currency.Eur), Today, "Reason", [])))
            .ShouldFailWith(BudgetErrorCodes.BucketCurrencyMismatch, ErrorKind.BusinessRule);
        (await Waive(plan, 0m)).ShouldFailWith(ErrorCodes.MoneyAmountNotPositive, ErrorKind.Validation);

        new WaiveShortfallValidator().Validate(new WaiveShortfall(Guid.CreateVersion7(), plan.Value, Pln(1m), Today, null, []))
            .Errors.ShouldContain(error => error.PropertyName == "Note" && error.ErrorCode == "NotEmptyValidator");
        new WaiveShortfallValidator().Validate(new WaiveShortfall(Guid.CreateVersion7(), plan.Value, Pln(1m), Today, "Reason", [])).IsValid.ShouldBeTrue();
        new PausePlanValidator().Validate(new PausePlan(Guid.CreateVersion7(), plan.Value, January, null, (ContractPauseMode)7, Today, null, []))
            .Errors.ShouldContain(error => error.PropertyName == "Mode");
        new ChangePlanPauseValidator().Validate(new ChangePlanPause(Guid.CreateVersion7(), plan.Value, Guid.Empty, January, null, ContractPauseMode.Skip, Today, null, []))
            .Errors.ShouldContain(error => error.PropertyName == "PauseId" && error.ErrorCode == "NotEmptyValidator");
    }

    [Fact]
    public async Task RepeatedPauseAndWaiver_WriteNothing_AndOtherContent_IsIdReused()
    {
        var plan = await NewPlan(holiday, 500m, November);
        var pause = new PausePlan(Guid.CreateVersion7(), plan.Value, January, null, ContractPauseMode.Skip, Today, null, []);
        var waiver = new WaiveShortfall(Guid.CreateVersion7(), plan.Value, Pln(100m), Today, "Reason", []);
        (await Send(pause)).IsSuccess.ShouldBeTrue();
        (await Send(waiver)).IsSuccess.ShouldBeTrue();
        StoreCommits();
        var commits = ledger.Commits.Count;

        (await Send(pause)).IsSuccess.ShouldBeTrue();
        (await Send(waiver)).IsSuccess.ShouldBeTrue();
        (await Send(pause with { Mode = ContractPauseMode.Shift })).ShouldFailWith(BudgetErrorCodes.EntryIdReused, ErrorKind.Conflict);
        (await Send(waiver with { Amount = Pln(101m) })).ShouldFailWith(BudgetErrorCodes.EntryIdReused, ErrorKind.Conflict);
        ledger.Commits.Count.ShouldBe(commits);
    }

    [Fact]
    public async Task APlanWithAPauseOrWaiver_AndAPauseWithAChange_HaveDependents_UntilThoseAreReversed()
    {
        var plan = await NewPlan(holiday, 500m, January);
        var pause = Guid.CreateVersion7();
        (await Send(new PausePlan(pause, plan.Value, February, null, ContractPauseMode.Skip, Today, null, []))).IsSuccess.ShouldBeTrue();
        StoreCommits();
        (await ChangePause(plan, pause, February, February, ContractPauseMode.Skip)).IsSuccess.ShouldBeTrue();
        StoreCommits();
        var change = store.Journal.OfType<PlanPauseChanged>().Single();

        (await Reverse(plan.Value)).ShouldFailWith(BudgetErrorCodes.EntryHasDependents, ErrorKind.BusinessRule);
        (await Reverse(pause)).ShouldFailWith(BudgetErrorCodes.EntryHasDependents, ErrorKind.BusinessRule);
        (await Reverse(change.Id)).IsSuccess.ShouldBeTrue();
        StoreCommits();
        (await Reverse(pause)).IsSuccess.ShouldBeTrue();
        StoreCommits();
        (await Reverse(plan.Value)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task APlanWithAWaiver_HasDependents()
    {
        var plan = await NewPlan(holiday, 500m, November);
        (await Waive(plan, 10m)).IsSuccess.ShouldBeTrue();
        StoreCommits();

        (await Reverse(plan.Value)).ShouldFailWith(BudgetErrorCodes.EntryHasDependents, ErrorKind.BusinessRule);
    }

    [Fact]
    public async Task ABucketWithAPausedPlan_IsNotArchived()
    {
        var plan = await NewPlan(holiday, 100m, January);
        (await Pause(plan, January, null, ContractPauseMode.Skip)).IsSuccess.ShouldBeTrue();
        StoreCommits();

        (await new ArchiveBucketHandler(new BudgetStateLoader(ledger, store), ledger, clock).Handle(new ArchiveBucket(holiday.Id), Cancellation))
            .ShouldFailWith(BudgetErrorCodes.BucketHasActivePlan, ErrorKind.BusinessRule);
    }

    [Fact]
    public async Task TheJournal_ShowsPausesAndWaiversWithTheirPlan_AndTheBucketFilterKeepsThem()
    {
        var plan = await NewPlan(holiday, 500m, November);
        var pause = Guid.CreateVersion7();
        (await Send(new PausePlan(pause, plan.Value, January, null, ContractPauseMode.Shift, Today, null, []))).IsSuccess.ShouldBeTrue();
        StoreCommits();
        (await ChangePause(plan, pause, January, new YearMonth(2026, 3), ContractPauseMode.Skip)).IsSuccess.ShouldBeTrue();
        (await Waive(plan, 100m)).IsSuccess.ShouldBeTrue();
        StoreCommits();

        var page = (await new GetJournalHandler(new BudgetStateLoader(ledger, store)).Handle(new Contracts.V1.Journal.GetJournal(Limit: 50, BucketId: holiday.Id), Cancellation)).Value;
        var entries = page.Entries.ToDictionary(entry => entry.Type.ToString());

        var paused = entries["PlanPaused"];
        (paused.Id, paused.BucketId, paused.PlanId, paused.PauseId, paused.FromMonth, paused.ToMonth, paused.PauseMode)
            .ShouldBe((pause, holiday.Id, plan.Value, pause, January, null, ContractPauseMode.Shift));
        var changed = entries["PlanPauseChanged"];
        (changed.BucketId, changed.PlanId, changed.PauseId, changed.FromMonth, changed.ToMonth, changed.PauseMode)
            .ShouldBe((holiday.Id, plan.Value, pause, January, new YearMonth(2026, 3), ContractPauseMode.Skip));
        var waived = entries["ShortfallWaived"];
        (waived.BucketId, waived.PlanId, waived.Amount, waived.PauseId).ShouldBe((holiday.Id, plan.Value, Pln(100m), null));

        var cushions = (await new GetJournalHandler(new BudgetStateLoader(ledger, store)).Handle(new Contracts.V1.Journal.GetJournal(Limit: 50, BucketId: cushion.Id), Cancellation)).Value;
        cushions.Entries.ShouldBeEmpty();
    }

    // A plan created through the handler, dated two weeks back, and stored so later commands see it.
    private async Task<DomainPlans.PlanId> NewPlan(Bucket bucket, decimal amount, YearMonth from, YearMonth? to = null)
    {
        var id = Guid.CreateVersion7();
        (await Send(new CreatePlan(id, bucket.Id, Pln(amount), from, to, Today.AddDays(-14), null, []))).IsSuccess.ShouldBeTrue();
        StoreCommits();
        return DomainPlans.PlanId.From(id);
    }

    private void StoreCommits()
    {
        foreach (var commit in ledger.Commits.Skip(stored))
        {
            store.Journal.AddRange(commit.Appends.Cast<JournalEntry>());
        }

        stored = ledger.Commits.Count;
    }

    private BudgetState State() => BudgetState.Replay(store.Buckets, store.Journal, Today);

    private DomainPlans.PlanStanding Standing(DomainPlans.PlanId plan) => State().StandingOf(State().FindPlan(plan)!);

    private Task<FluentResults.Result> Pause(DomainPlans.PlanId plan, YearMonth from, YearMonth? to, ContractPauseMode mode) =>
        Send(new PausePlan(Guid.CreateVersion7(), plan.Value, from, to, mode, Today, null, []));

    private Task<FluentResults.Result> ChangePause(DomainPlans.PlanId plan, Guid pause, YearMonth from, YearMonth? to, ContractPauseMode mode) =>
        Send(new ChangePlanPause(Guid.CreateVersion7(), plan.Value, pause, from, to, mode, Today, null, []));

    private Task<FluentResults.Result> Waive(DomainPlans.PlanId plan, decimal amount) =>
        Send(new WaiveShortfall(Guid.CreateVersion7(), plan.Value, Pln(amount), Today, "Reason", []));

    private Task<FluentResults.Result> Send(CreatePlan command) => new CreatePlanHandler(Writer()).Handle(command, Cancellation).AsTask();

    private Task<FluentResults.Result> Send(PausePlan command) => new PausePlanHandler(Writer()).Handle(command, Cancellation).AsTask();

    private Task<FluentResults.Result> Send(ChangePlanPause command) => new ChangePlanPauseHandler(Writer()).Handle(command, Cancellation).AsTask();

    private Task<FluentResults.Result> Send(WaiveShortfall command) => new WaiveShortfallHandler(Writer()).Handle(command, Cancellation).AsTask();

    private Task<FluentResults.Result> Reverse(Guid target) =>
        new ReverseEntryHandler(Writer(), new JournalEntryDependencies()).Handle(new ReverseEntry(Guid.CreateVersion7(), target, Today, null, []), Cancellation).AsTask();

    private JournalWriter Writer() => new(new BudgetStateLoader(ledger, store), ledger, new FakeOwnerContext(), clock);

    private static Money Pln(decimal amount) => Money.From(amount, Currency.Pln);
}
