using FinTrack.Budget.Application.Buckets;
using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Application.Plans;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.Plans;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Shared.Kernel.Time;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Application.Tests.Plans;

/// <summary>
/// <c>CreatePlan</c>, <c>ChangePlanAmount</c>, <c>SetPlanEnd</c>, contributions linked to a plan (plan 1b B7, B9), and the plan
/// rules of <c>ReverseEntry</c> and <c>ArchiveBucket</c> (B5, B17) against in-memory fakes, with invariant 6 (B8) as the writer
/// checks it. Today is 15 January 2026: December 2025 is last month.
/// </summary>
public class PlanHandlerTests
{
    private static readonly DateOnly Today = new(2026, 1, 15);
    private static readonly YearMonth January = new(2026, 1);
    private static readonly YearMonth December = new(2025, 12);
    private static readonly YearMonth November = new(2025, 11);

    private readonly CallLog log = new();
    private readonly FakeLedger ledger;
    private readonly FakeBudgetStore store;
    private readonly FakeClock clock = new();
    private readonly Bucket cushion;
    private readonly Bucket holiday;
    private int stored;

    public PlanHandlerTests()
    {
        ledger = new FakeLedger(log);
        store = new FakeBudgetStore(log) { Settings = StoredSettings.Of(Currency.Pln, "Europe/Warsaw", "pl-PL") };
        cushion = StoredBuckets.Of(Guid.CreateVersion7(), "Cushion", Currency.Pln, 1, FakeClock.DefaultStart, BucketStatus.Active);
        holiday = StoredBuckets.Of(Guid.CreateVersion7(), "Holiday", Currency.Pln, 1, FakeClock.DefaultStart, BucketStatus.Active);
        store.Buckets.AddRange([cushion, holiday]);
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreatePlan_AppendsOnePlanCreated_UnderTheVersionReadFirst()
    {
        ledger.Version = 4;
        var id = Guid.CreateVersion7();

        var result = await Send(new CreatePlan(id, cushion.Id, Pln(500.5m), November, new YearMonth(2026, 12), Today.AddDays(-1), "Cushion fund", ["save"]));

        result.IsSuccess.ShouldBeTrue();
        log.Calls.ShouldBe(["version", "settings", "buckets", "journal", "version", "commit"]);
        var commit = ledger.Commits.ShouldHaveSingleItem();
        commit.ExpectedVersion.ShouldBe(4);
        commit.Saves.ShouldBeEmpty();
        var created = commit.Appends.ShouldHaveSingleItem().ShouldBeOfType<PlanCreated>();
        created.Id.ShouldBe(id);
        created.BucketId.ShouldBe(cushion.BucketId);
        created.Amount.ShouldBe(Pln(500.50m));
        created.ValidFrom.ShouldBe(November);
        created.ValidTo.ShouldBe(new YearMonth(2026, 12));
        created.OccurredOn.ShouldBe(Today.AddDays(-1));
        created.Note.ShouldBe("Cushion fund");
        created.Tags.ShouldBe(["save"]);
    }

    [Fact]
    public async Task CreatePlan_NeedsAnActiveBucketOfTheUser_InTheAmountsCurrency_AndItsOwnRules()
    {
        var unallocated = Bucket.Unallocated(BucketId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, Currency.Pln);
        var old = StoredBuckets.Of(Guid.CreateVersion7(), "Old", Currency.Pln, 2, FakeClock.DefaultStart, BucketStatus.Archived);
        var crypto = StoredBuckets.Of(Guid.CreateVersion7(), "Crypto", Currency.Pln, FakeClock.DefaultStart, BucketStatus.Active, BucketKind.Investment, InvestmentProfile.Crypto, BucketSystemRole.None);
        store.Buckets.AddRange([unallocated, old, crypto]);

        (await Create(Guid.CreateVersion7(), Pln(1m))).ShouldFailWith(BudgetErrorCodes.BucketNotFound, ErrorKind.NotFound);
        (await Create(unallocated.Id, Pln(1m))).ShouldFailWith(BudgetErrorCodes.BucketSystemBucket, ErrorKind.BusinessRule);
        (await Create(old.Id, Pln(1m))).ShouldFailWith(BudgetErrorCodes.BucketArchived, ErrorKind.BusinessRule);
        (await Create(cushion.Id, Money.From(1m, Currency.Eur))).ShouldFailWith(BudgetErrorCodes.BucketCurrencyMismatch, ErrorKind.BusinessRule);
        (await Create(cushion.Id, Pln(0m))).ShouldFailWith(ErrorCodes.MoneyAmountNotPositive, ErrorKind.Validation);
        (await Send(new CreatePlan(Guid.CreateVersion7(), cushion.Id, Pln(1m), January, December, Today, null, [])))
            .ShouldFailWith(BudgetErrorCodes.PlanMonthsInvalid, ErrorKind.Validation);
        (await Send(new CreatePlan(Guid.CreateVersion7(), cushion.Id, Pln(1m), January, null, Today.AddDays(1), null, [])))
            .ShouldFailWith(BudgetErrorCodes.EntryDateInFuture, ErrorKind.BusinessRule);
        ledger.Commits.ShouldBeEmpty();

        // Plans on investment buckets work on net invested (B7).
        (await Create(crypto.Id, Pln(1m))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task APlanMayStartInThePast_AndItsMonthsAreDueAtOnce()
    {
        var plan = await NewPlan(cushion, 500m, November);

        var standing = State().StandingOf(State().FindPlan(plan)!);
        standing.DueNow.ShouldBe(Pln(1500m));
        standing.Shortfall.ShouldBe(Pln(1000m));
    }

    [Fact]
    public async Task ARepeatedCreate_WritesNothing_AndOtherContent_IsIdReused()
    {
        var id = Guid.CreateVersion7();
        var command = new CreatePlan(id, cushion.Id, Pln(100m), January, null, Today, null, []);
        (await Send(command)).IsSuccess.ShouldBeTrue();
        StoreCommits();

        (await Send(command)).IsSuccess.ShouldBeTrue();
        (await Send(command with { Amount = Pln(101m) })).ShouldFailWith(BudgetErrorCodes.EntryIdReused, ErrorKind.Conflict);
        ledger.Commits.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ChangePlanAmount_FromTheCurrentMonth_IsANewVersion()
    {
        var plan = await NewPlan(cushion, 500m, November);

        (await ChangeAmount(plan, 800m, January)).IsSuccess.ShouldBeTrue();

        var changed = ledger.Commits[^1].Appends.ShouldHaveSingleItem().ShouldBeOfType<PlanAmountChanged>();
        changed.PlanId.ShouldBe(plan);
        changed.Amount.ShouldBe(Pln(800m));
        changed.From.ShouldBe(January);
        StoreCommits();
        PlanSchedule.Accrual(State().FindPlan(plan)!, January).ShouldBe(Pln(800m));
        PlanSchedule.Accrual(State().FindPlan(plan)!, December).ShouldBe(Pln(500m));
    }

    [Fact]
    public async Task AVersionStartingLastMonth_IsRefusedWithTheMonth_UnlessTheAmountIsTheSame()
    {
        var plan = await NewPlan(cushion, 500m, November);

        (await ChangeAmount(plan, 800m, December)).ShouldFailWith(BudgetErrorCodes.PlanPastPeriodChanged, ErrorKind.BusinessRule)
            .Parameters["month"].ShouldBe("2025-12");
        (await ChangeAmount(plan, 800m, November)).ShouldFailWith(BudgetErrorCodes.PlanPastPeriodChanged, ErrorKind.BusinessRule)
            .Parameters["month"].ShouldBe("2025-11");
        ledger.Commits.Count.ShouldBe(1);

        (await ChangeAmount(plan, 500m, December)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ChangePlanAmount_NeedsAPlanThatHasNotEnded_InItsCurrency_FromAMonthOfThePlan()
    {
        var plan = await NewPlan(cushion, 500m, January, new YearMonth(2026, 6));
        var ended = await NewPlan(holiday, 100m, November, December);

        (await ChangeAmount(Domain.Plans.PlanId.From(Guid.CreateVersion7()), 1m, January)).ShouldFailWith(BudgetErrorCodes.PlanNotFound, ErrorKind.NotFound);
        (await ChangeAmount(ended, 200m, January)).ShouldFailWith(BudgetErrorCodes.PlanEnded, ErrorKind.BusinessRule);
        (await ChangeAmount(plan, 200m, December)).ShouldFailWith(BudgetErrorCodes.PlanMonthsInvalid, ErrorKind.Validation);
        (await ChangeAmount(plan, 200m, new YearMonth(2026, 7))).ShouldFailWith(BudgetErrorCodes.PlanMonthsInvalid, ErrorKind.Validation);
        (await Send(new ChangePlanAmount(Guid.CreateVersion7(), plan.Value, Money.From(1m, Currency.Eur), January, Today, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketCurrencyMismatch, ErrorKind.BusinessRule);
        (await ChangeAmount(plan, 0m, January)).ShouldFailWith(ErrorCodes.MoneyAmountNotPositive, ErrorKind.Validation);
        ledger.Commits.Count.ShouldBe(2);

        (await ChangeAmount(plan, 200m, new YearMonth(2026, 6))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task SetPlanEnd_AsOfLastMonth_IsAllowed_AndEarlierIsRefusedWithTheMonth()
    {
        var plan = await NewPlan(cushion, 500m, new YearMonth(2025, 10));

        (await SetEnd(plan, November)).ShouldFailWith(BudgetErrorCodes.PlanPastPeriodChanged, ErrorKind.BusinessRule).Parameters["month"].ShouldBe("2025-12");
        (await SetEnd(plan, December)).IsSuccess.ShouldBeTrue();

        var ended = ledger.Commits[^1].Appends.ShouldHaveSingleItem().ShouldBeOfType<PlanEndChanged>();
        ended.PlanId.ShouldBe(plan);
        ended.ValidTo.ShouldBe(December);
        StoreCommits();

        var standing = State().StandingOf(State().FindPlan(plan)!);
        standing.Status.ShouldBe(PlanStatus.Ended);
        standing.Shortfall.ShouldBe(Pln(1500m));

        // Extending it again is fine from the current month; it never revives a past month.
        (await SetEnd(plan, null)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ExtendingAPlanThatEndedBeforeLastMonth_IsRefused()
    {
        var plan = await NewPlan(cushion, 500m, new YearMonth(2025, 9), November);

        (await SetEnd(plan, null)).ShouldFailWith(BudgetErrorCodes.PlanPastPeriodChanged, ErrorKind.BusinessRule).Parameters["month"].ShouldBe("2025-12");
        (await SetEnd(plan, new YearMonth(2026, 3))).ShouldFailWith(BudgetErrorCodes.PlanPastPeriodChanged, ErrorKind.BusinessRule);
    }

    [Fact]
    public async Task SetPlanEnd_NeedsAPlan_AndAnEndNotBeforeItsStart()
    {
        var plan = await NewPlan(cushion, 500m, new YearMonth(2026, 3));

        (await SetEnd(plan, new YearMonth(2026, 2))).ShouldFailWith(BudgetErrorCodes.PlanMonthsInvalid, ErrorKind.Validation);
        (await SetEnd(Domain.Plans.PlanId.From(Guid.CreateVersion7()), null)).ShouldFailWith(BudgetErrorCodes.PlanNotFound, ErrorKind.NotFound);
        (await SetEnd(plan, new YearMonth(2026, 3))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task APlanOfAnArchivedBucket_CannotChange()
    {
        var plan = await NewPlan(holiday, 100m, December, December);
        await Deposit(holiday, 100m, plan, -20);
        await Spend(holiday, 100m, -10);
        (await Archive(holiday.Id)).IsSuccess.ShouldBeTrue();
        StoreCommits();

        (await SetEnd(plan, null)).ShouldFailWith(BudgetErrorCodes.BucketArchived, ErrorKind.BusinessRule);
        (await ChangeAmount(plan, 1m, January)).ShouldFailWith(BudgetErrorCodes.BucketArchived, ErrorKind.BusinessRule);
    }

    [Fact]
    public async Task LinkedDepositsAndTransfers_PayThePlan_AndMustNameAPlanOfTheReceivingBucket()
    {
        var plan = await NewPlan(holiday, 500m, December);
        var other = await NewPlan(cushion, 100m, January);
        await Deposit(cushion, 1000m, null, -30);

        (await Deposit(holiday, 300m, plan, -20)).IsSuccess.ShouldBeTrue();
        (await Send(new AddTransfer(Guid.CreateVersion7(), Pln(400m), Pln(400m), null, Today.AddDays(-1), cushion.Id, null, holiday.Id, null, null, [], plan.Value)))
            .IsSuccess.ShouldBeTrue();
        StoreCommits();

        var deposit = store.Journal.OfType<Deposit>().Single(entry => entry.PlanId is not null);
        deposit.PlanId.ShouldBe(plan);
        store.Journal.OfType<Transfer>().Single().PlanId.ShouldBe(plan);
        var standing = State().StandingOf(State().FindPlan(plan)!);
        standing.Credit.ShouldBe(Pln(700m));
        standing.DueNow.ShouldBe(Pln(300m));
        standing.Shortfall.ShouldBe(Pln(0m));

        (await Deposit(holiday, 1m, other, 0)).ShouldFailWith(BudgetErrorCodes.PlanBucketMismatch, ErrorKind.BusinessRule);
        (await Deposit(holiday, 1m, Domain.Plans.PlanId.From(Guid.CreateVersion7()), 0)).ShouldFailWith(BudgetErrorCodes.PlanNotFound, ErrorKind.NotFound);
        (await Send(new AddTransfer(Guid.CreateVersion7(), Pln(1m), Pln(1m), null, Today, holiday.Id, null, cushion.Id, null, null, [], plan.Value)))
            .ShouldFailWith(BudgetErrorCodes.PlanBucketMismatch, ErrorKind.BusinessRule);
        (await Send(new AddDeposit(Guid.CreateVersion7(), Pln(1m), Today, null, Currency.Pln, null, [], plan.Value)))
            .ShouldFailWith(BudgetErrorCodes.PlanBucketMismatch, ErrorKind.BusinessRule);
    }

    [Fact]
    public async Task ARepeatedLinkedDeposit_WritesNothing_AndWithoutItsPlan_IsIdReused()
    {
        var plan = await NewPlan(holiday, 500m, January);
        var command = new AddDeposit(Guid.CreateVersion7(), Pln(10m), Today, holiday.Id, null, null, [], plan.Value);
        (await Send(command)).IsSuccess.ShouldBeTrue();
        StoreCommits();
        var commits = ledger.Commits.Count;

        (await Send(command)).IsSuccess.ShouldBeTrue();
        (await Send(command with { PlanId = null })).ShouldFailWith(BudgetErrorCodes.EntryIdReused, ErrorKind.Conflict);
        ledger.Commits.Count.ShouldBe(commits);
    }

    [Fact]
    public async Task ReversingAPlanWithChangesOrContributions_HasDependents_UntilTheyAreReversed()
    {
        var plan = await NewPlan(holiday, 500m, January);
        (await ChangeAmount(plan, 600m, new YearMonth(2026, 3))).IsSuccess.ShouldBeTrue();
        StoreCommits();
        var change = store.Journal.OfType<PlanAmountChanged>().Single();

        (await Reverse(plan.Value)).ShouldFailWith(BudgetErrorCodes.EntryHasDependents, ErrorKind.BusinessRule);
        (await Reverse(change.Id)).IsSuccess.ShouldBeTrue();
        StoreCommits();

        await Deposit(holiday, 10m, plan, 0);
        var deposit = store.Journal.OfType<Deposit>().Single();
        (await Reverse(plan.Value)).ShouldFailWith(BudgetErrorCodes.EntryHasDependents, ErrorKind.BusinessRule);
        (await Reverse(deposit.Id)).IsSuccess.ShouldBeTrue();
        StoreCommits();

        (await Reverse(plan.Value)).IsSuccess.ShouldBeTrue();
        StoreCommits();
        State().Plans.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReversingAPlanWithPastMonths_OrAPastVersion_IsRefusedWithTheMonth()
    {
        var plan = await NewPlan(cushion, 500m, November);
        (await Reverse(plan.Value)).ShouldFailWith(BudgetErrorCodes.PlanPastPeriodChanged, ErrorKind.BusinessRule).Parameters["month"].ShouldBe("2025-11");

        // A version written in December, from December: reversing it now changes December.
        var version = PlanAmountChanged.Create(Domain.Journal.JournalEntryId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, plan, Pln(700m), December,
            new DateOnly(2025, 12, 3), new DateOnly(2025, 12, 3), EntryInfo.None, FakeClock.DefaultStart.AddDays(-43)).Value;
        store.Journal.Add(version);
        (await Reverse(version.Id)).ShouldFailWith(BudgetErrorCodes.PlanPastPeriodChanged, ErrorKind.BusinessRule).Parameters["month"].ShouldBe("2025-12");
    }

    [Fact]
    public async Task ReversingAPlanThatStartsThisMonth_OrLater_IsAllowed()
    {
        var plan = await NewPlan(cushion, 500m, January);

        (await Reverse(plan.Value)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ABucketWithAnActivePlan_IsNotArchived_NorWithAnEndedOneThatHasAShortfall()
    {
        var plan = await NewPlan(holiday, 100m, December);
        (await Archive(holiday.Id)).ShouldFailWith(BudgetErrorCodes.BucketHasActivePlan, ErrorKind.BusinessRule);

        (await SetEnd(plan, December)).IsSuccess.ShouldBeTrue();
        StoreCommits();
        (await Archive(holiday.Id)).ShouldFailWith(BudgetErrorCodes.BucketHasActivePlan, ErrorKind.BusinessRule);

        await Deposit(holiday, 100m, plan, -5);
        (await Archive(holiday.Id)).ShouldFailWith(BudgetErrorCodes.BucketNotEmpty, ErrorKind.BusinessRule);
        await Spend(holiday, 100m, -1);
        (await Archive(holiday.Id)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task TheJournal_ShowsPlanEntriesWithTheirPlanAndMonths_AndTheBucketFilterKeepsThem()
    {
        var plan = await NewPlan(holiday, 500m, December, new YearMonth(2026, 6));
        (await ChangeAmount(plan, 600m, new YearMonth(2026, 2))).IsSuccess.ShouldBeTrue();
        (await SetEnd(plan, null)).IsSuccess.ShouldBeTrue();
        (await Deposit(holiday, 300m, plan, 0)).IsSuccess.ShouldBeTrue();
        StoreCommits();
        await Deposit(cushion, 50m, null, 0);

        var page = (await new GetJournalHandler(new BudgetStateLoader(ledger, store))
            .Handle(new Contracts.V1.Journal.GetJournal(Limit: 50), Cancellation)).Value;
        var entries = page.Entries.ToDictionary(entry => entry.Type.ToString() + (entry.BucketId == cushion.Id ? "-cushion" : string.Empty));

        var created = entries["PlanCreated"];
        (created.Id, created.BucketId, created.Amount, created.PlanId, created.FromMonth, created.ToMonth)
            .ShouldBe((plan.Value, holiday.Id, Pln(500m), plan.Value, December, new YearMonth(2026, 6)));
        var changed = entries["PlanAmountChanged"];
        (changed.BucketId, changed.Amount, changed.PlanId, changed.FromMonth, changed.ToMonth).ShouldBe((holiday.Id, Pln(600m), plan.Value, new YearMonth(2026, 2), null));
        var ended = entries["PlanEndChanged"];
        (ended.BucketId, ended.Amount, ended.PlanId, ended.FromMonth, ended.ToMonth).ShouldBe((holiday.Id, null, plan.Value, null, null));
        entries["Deposit"].PlanId.ShouldBe(plan.Value);
        entries["Deposit-cushion"].PlanId.ShouldBeNull();

        var holidays = (await new GetJournalHandler(new BudgetStateLoader(ledger, store))
            .Handle(new Contracts.V1.Journal.GetJournal(Limit: 50, BucketId: holiday.Id), Cancellation)).Value;
        holidays.Entries.Select(entry => entry.Type.ToString()).ShouldBe(["Deposit", "PlanEndChanged", "PlanAmountChanged", "PlanCreated"]);
        var cushions = (await new GetJournalHandler(new BudgetStateLoader(ledger, store))
            .Handle(new Contracts.V1.Journal.GetJournal(Limit: 50, BucketId: cushion.Id), Cancellation)).Value;
        cushions.Entries.ShouldHaveSingleItem().Type.ToString().ShouldBe("Deposit");
    }

    [Fact]
    public void Validators_NeedTheIds()
    {
        new CreatePlanValidator().Validate(new CreatePlan(Guid.CreateVersion7(), Guid.Empty, Pln(1m), January, null, Today, null, []))
            .Errors.ShouldContain(error => error.PropertyName == "BucketId" && error.ErrorCode == "NotEmptyValidator");
        new ChangePlanAmountValidator().Validate(new ChangePlanAmount(Guid.CreateVersion7(), Guid.Empty, Pln(1m), January, Today, null, []))
            .Errors.ShouldContain(error => error.PropertyName == "PlanId" && error.ErrorCode == "NotEmptyValidator");
        new SetPlanEndValidator().Validate(new SetPlanEnd(Guid.CreateVersion7(), Guid.Empty, null, Today, null, []))
            .Errors.ShouldContain(error => error.PropertyName == "PlanId" && error.ErrorCode == "NotEmptyValidator");
        new SetPlanEndValidator().Validate(new SetPlanEnd(Guid.CreateVersion7(), Guid.CreateVersion7(), null, Today, " x", []))
            .Errors.ShouldContain(error => error.PropertyName == "Note");
        new AddDepositValidator().Validate(new AddDeposit(Guid.CreateVersion7(), Pln(1m), Today, cushion.Id, null, null, [], Guid.Empty))
            .Errors.ShouldContain(error => error.PropertyName == "PlanId");
        new AddTransferValidator().Validate(new AddTransfer(Guid.CreateVersion7(), Pln(1m), Pln(1m), null, Today, cushion.Id, null, holiday.Id, null, null, [], Guid.Empty))
            .Errors.ShouldContain(error => error.PropertyName == "PlanId");
        new CreatePlanValidator().Validate(new CreatePlan(Guid.CreateVersion7(), cushion.Id, Pln(1m), January, null, Today, "Fund", ["a"])).IsValid.ShouldBeTrue();
    }

    // A plan created through the handler and stored, so later commands see it.
    private async Task<PlanId> NewPlan(Bucket bucket, decimal amount, YearMonth from, YearMonth? to = null)
    {
        var id = Guid.CreateVersion7();
        (await Send(new CreatePlan(id, bucket.Id, Pln(amount), from, to, Today.AddDays(-14), null, []))).IsSuccess.ShouldBeTrue();
        StoreCommits();
        return Domain.Plans.PlanId.From(id);
    }

    private void StoreCommits()
    {
        foreach (var commit in ledger.Commits.Skip(stored))
        {
            store.Journal.AddRange(commit.Appends.Cast<JournalEntry>());
            foreach (var bucket in commit.Saves.OfType<Bucket>())
            {
                var index = store.Buckets.FindIndex(existing => existing.Id == bucket.Id);
                if (index < 0)
                {
                    store.Buckets.Add(bucket);
                }
                else
                {
                    store.Buckets[index] = bucket;
                }
            }
        }

        stored = ledger.Commits.Count;
    }

    private BudgetState State() => BudgetState.Replay(store.Buckets, store.Journal, Today);

    private Task<FluentResults.Result> Create(Guid bucket, Money amount) =>
        Send(new CreatePlan(Guid.CreateVersion7(), bucket, amount, January, null, Today, null, []));

    private Task<FluentResults.Result> ChangeAmount(PlanId plan, decimal amount, YearMonth from) =>
        Send(new ChangePlanAmount(Guid.CreateVersion7(), plan.Value, Pln(amount), from, Today, null, []));

    private Task<FluentResults.Result> SetEnd(PlanId plan, YearMonth? to) =>
        Send(new SetPlanEnd(Guid.CreateVersion7(), plan.Value, to, Today, null, []));

    // A deposit (linked when a plan is given), stored when accepted.
    private async Task<FluentResults.Result> Deposit(Bucket bucket, decimal amount, PlanId? plan, int day)
    {
        var result = await Send(new AddDeposit(Guid.CreateVersion7(), Pln(amount), Today.AddDays(day), bucket.Id, null, null, [], plan?.Value));
        StoreCommits();
        return result;
    }

    private async Task Spend(Bucket bucket, decimal amount, int day)
    {
        (await new AddSpendHandler(new BucketEntryWriter(new BudgetStateLoader(ledger, store), ledger, new FakeOwnerContext(), clock))
            .Handle(new AddSpend(Guid.CreateVersion7(), Pln(amount), Today.AddDays(day), bucket.Id, null, null, []), Cancellation)).IsSuccess.ShouldBeTrue();
        StoreCommits();
    }

    private Task<FluentResults.Result> Send(CreatePlan command) => new CreatePlanHandler(Writer()).Handle(command, Cancellation).AsTask();

    private Task<FluentResults.Result> Send(ChangePlanAmount command) => new ChangePlanAmountHandler(Writer()).Handle(command, Cancellation).AsTask();

    private Task<FluentResults.Result> Send(SetPlanEnd command) => new SetPlanEndHandler(Writer()).Handle(command, Cancellation).AsTask();

    private Task<FluentResults.Result> Send(AddDeposit command) =>
        new AddDepositHandler(new BucketEntryWriter(new BudgetStateLoader(ledger, store), ledger, new FakeOwnerContext(), clock)).Handle(command, Cancellation).AsTask();

    private Task<FluentResults.Result> Send(AddTransfer command) => new AddTransferHandler(Writer()).Handle(command, Cancellation).AsTask();

    private Task<FluentResults.Result> Reverse(Guid target) =>
        new ReverseEntryHandler(Writer(), new JournalEntryDependencies()).Handle(new ReverseEntry(Guid.CreateVersion7(), target, Today, null, []), Cancellation).AsTask();

    private Task<FluentResults.Result> Archive(Guid bucket) =>
        new ArchiveBucketHandler(new BudgetStateLoader(ledger, store), ledger, clock).Handle(new ArchiveBucket(bucket), Cancellation).AsTask();

    private JournalWriter Writer() => new(new BudgetStateLoader(ledger, store), ledger, new FakeOwnerContext(), clock);

    private static Money Pln(decimal amount) => Money.From(amount, Currency.Pln);
}
