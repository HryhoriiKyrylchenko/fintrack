using FinTrack.Budget.Application;
using FinTrack.Budget.Application.Buckets;
using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Application.Plans;
using FinTrack.Budget.Application.Settings;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Plans;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Shared.Kernel.Time;
using FinTrack.Tests.Common;
using FluentResults;
using Microsoft.Extensions.DependencyInjection;
using BucketKind = FinTrack.Budget.Contracts.V1.Buckets.BucketKind;

namespace FinTrack.Budget.Infrastructure.IntegrationTests.Journal;

/// <summary>
/// Contribution plans (plan 1b step 9) through the real ledger and MongoDB: 4.5's example replayed from the store, a version
/// from last month refused with its month, an ended plan that keeps its shortfall, a prepayment; every refusal writes
/// nothing, and another owner can neither change a plan nor pay into it.
/// </summary>
public sealed class PlanTests(BudgetFixture fixture)
{
    // The fixture's clock is FakeClock.DefaultStart: 2026-01-15 in Warsaw, so December 2025 is last month.
    private static readonly DateOnly Today = new(2026, 1, 15);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheExampleOf45_HoldsFromTheStore_AndAPrepaymentSettlesLaterMonths()
    {
        var owner = await NewOwnerAsync();
        var holiday = await CreateBucketAsync(owner, "Holiday");
        var plan = Guid.CreateVersion7();
        (await SendAsync<CreatePlanHandler, CreatePlan>(owner, new CreatePlan(plan, holiday, Pln(500m), new YearMonth(2025, 12), null, Today.AddDays(-40), "Holiday fund", [])))
            .IsSuccess.ShouldBeTrue();
        await DepositAsync(owner, holiday, 300m, -30, plan);

        var standing = await StandingAsync(owner, plan);
        standing.DueNow.ShouldBe(Pln(700m));
        standing.Shortfall.ShouldBe(Pln(200m));

        await DepositAsync(owner, holiday, 1200m, 0, plan);
        standing = await StandingAsync(owner, plan);
        standing.DueNow.ShouldBe(Pln(0m));
        standing.Prepaid.ShouldBe(Pln(500m));
    }

    [Fact]
    public async Task EveryRefusal_WritesNothing()
    {
        var owner = await NewOwnerAsync();
        var holiday = await CreateBucketAsync(owner, "Holiday");
        var other = await CreateBucketAsync(owner, "Other");
        var plan = Guid.CreateVersion7();
        (await SendAsync<CreatePlanHandler, CreatePlan>(owner, new CreatePlan(plan, holiday, Pln(500m), new YearMonth(2025, 11), null, Today.AddDays(-50), null, [])))
            .IsSuccess.ShouldBeTrue();
        var before = await StateAsync(owner);

        (await SendAsync<ChangePlanAmountHandler, ChangePlanAmount>(owner, new ChangePlanAmount(Guid.CreateVersion7(), plan, Pln(600m), new YearMonth(2025, 12), Today, null, [])))
            .ShouldFailWith(BudgetErrorCodes.PlanPastPeriodChanged, ErrorKind.BusinessRule).Parameters["month"].ShouldBe("2025-12");
        (await SendAsync<SetPlanEndHandler, SetPlanEnd>(owner, new SetPlanEnd(Guid.CreateVersion7(), plan, new YearMonth(2025, 11), Today, null, [])))
            .ShouldFailWith(BudgetErrorCodes.PlanPastPeriodChanged, ErrorKind.BusinessRule).Parameters["month"].ShouldBe("2025-12");
        (await SendAsync<ReverseEntryHandler, ReverseEntry>(owner, new ReverseEntry(Guid.CreateVersion7(), plan, Today, null, [])))
            .ShouldFailWith(BudgetErrorCodes.PlanPastPeriodChanged, ErrorKind.BusinessRule);
        (await SendAsync<AddDepositHandler, AddDeposit>(owner, new AddDeposit(Guid.CreateVersion7(), Pln(1m), Today, other, null, null, [], plan)))
            .ShouldFailWith(BudgetErrorCodes.PlanBucketMismatch, ErrorKind.BusinessRule);
        (await SendAsync<ArchiveBucketHandler, ArchiveBucket>(owner, new ArchiveBucket(holiday)))
            .ShouldFailWith(BudgetErrorCodes.BucketHasActivePlan, ErrorKind.BusinessRule);
        (await SendAsync<CreatePlanHandler, CreatePlan>(owner, new CreatePlan(Guid.CreateVersion7(), holiday, Pln(1m), new YearMonth(2026, 2), new YearMonth(2026, 1), Today, null, [])))
            .ShouldFailWith(BudgetErrorCodes.PlanMonthsInvalid, ErrorKind.Validation);

        var after = await StateAsync(owner);
        after.Version.ShouldBe(before.Version);
        after.Journal.Count.ShouldBe(before.Journal.Count);
        after.Buckets.Single(bucket => bucket.Id == holiday).Status.ShouldBe(BucketStatus.Active);
    }

    [Fact]
    public async Task AnEndedPlan_KeepsItsShortfall_ReplayedFromTheStore()
    {
        var owner = await NewOwnerAsync();
        var holiday = await CreateBucketAsync(owner, "Holiday");
        var plan = Guid.CreateVersion7();
        (await SendAsync<CreatePlanHandler, CreatePlan>(owner, new CreatePlan(plan, holiday, Pln(100m), new YearMonth(2025, 11), null, Today.AddDays(-60), null, [])))
            .IsSuccess.ShouldBeTrue();
        (await SendAsync<SetPlanEndHandler, SetPlanEnd>(owner, new SetPlanEnd(Guid.CreateVersion7(), plan, new YearMonth(2025, 12), Today, "Goal changed", [])))
            .IsSuccess.ShouldBeTrue();

        var standing = await StandingAsync(owner, plan);
        standing.Status.ShouldBe(PlanStatus.Ended);
        standing.Shortfall.ShouldBe(Pln(200m));
        standing.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task AnotherOwner_CannotChangeAPlan_NorPayIntoIt()
    {
        var a = await NewOwnerAsync();
        var holiday = await CreateBucketAsync(a, "Holiday");
        var plan = Guid.CreateVersion7();
        (await SendAsync<CreatePlanHandler, CreatePlan>(a, new CreatePlan(plan, holiday, Pln(100m), new YearMonth(2026, 1), null, Today, null, []))).IsSuccess.ShouldBeTrue();
        var b = await NewOwnerAsync();
        var mine = await CreateBucketAsync(b, "Mine");

        (await SendAsync<ChangePlanAmountHandler, ChangePlanAmount>(b, new ChangePlanAmount(Guid.CreateVersion7(), plan, Pln(1m), new YearMonth(2026, 2), Today, null, [])))
            .ShouldFailWith(BudgetErrorCodes.PlanNotFound, ErrorKind.NotFound);
        (await SendAsync<SetPlanEndHandler, SetPlanEnd>(b, new SetPlanEnd(Guid.CreateVersion7(), plan, null, Today, null, [])))
            .ShouldFailWith(BudgetErrorCodes.PlanNotFound, ErrorKind.NotFound);
        (await SendAsync<AddDepositHandler, AddDeposit>(b, new AddDeposit(Guid.CreateVersion7(), Pln(1m), Today, mine, null, null, [], plan)))
            .ShouldFailWith(BudgetErrorCodes.PlanNotFound, ErrorKind.NotFound);
        (await SendAsync<CreatePlanHandler, CreatePlan>(b, new CreatePlan(Guid.CreateVersion7(), holiday, Pln(1m), new YearMonth(2026, 1), null, Today, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketNotFound, ErrorKind.NotFound);

        (await StateAsync(b)).StateAsOf(Today).Plans.ShouldBeEmpty();
        (await StateAsync(a)).StateAsOf(Today).Plans.ShouldHaveSingleItem().PlanId.Value.ShouldBe(plan);
    }

    private static Money Pln(decimal amount) => Money.From(amount, Currency.Pln);

    private async Task<OwnerId> NewOwnerAsync()
    {
        var owner = BudgetFixture.NewOwner();
        await using var scope = fixture.ScopeFor(owner);
        (await ActivatorUtilities.CreateInstance<SaveOwnerSettingsHandler>(scope.ServiceProvider)
            .Handle(new SaveOwnerSettings(Currency.Pln, "Europe/Warsaw", "pl-PL"), Cancellation)).IsSuccess.ShouldBeTrue();
        return owner;
    }

    private async Task<Guid> CreateBucketAsync(OwnerId owner, string name)
    {
        var id = Guid.CreateVersion7();
        (await SendAsync<CreateBucketHandler, CreateBucket>(owner, new CreateBucket(id, name, Currency.Pln, BucketKind.Savings, null))).IsSuccess.ShouldBeTrue();
        return id;
    }

    private async Task DepositAsync(OwnerId owner, Guid bucket, decimal amount, int day, Guid? plan) =>
        (await SendAsync<AddDepositHandler, AddDeposit>(owner, new AddDeposit(Guid.CreateVersion7(), Pln(amount), Today.AddDays(day), bucket, null, null, [], plan))).IsSuccess.ShouldBeTrue();

    private async Task<PlanStanding> StandingAsync(OwnerId owner, Guid plan)
    {
        var state = (await StateAsync(owner)).StateAsOf(Today);
        return state.StandingOf(state.FindPlan(PlanId.From(plan))!);
    }

    private async Task<Result> SendAsync<THandler, TCommand>(OwnerId owner, TCommand command)
        where THandler : Mediator.ICommandHandler<TCommand, Result>
        where TCommand : Mediator.ICommand<Result>
    {
        await using var scope = fixture.ScopeFor(owner);
        return await ActivatorUtilities.CreateInstance<THandler>(scope.ServiceProvider).Handle(command, Cancellation);
    }

    private async Task<BudgetSnapshot> StateAsync(OwnerId owner)
    {
        await using var scope = fixture.ScopeFor(owner);
        return (await scope.ServiceProvider.GetRequiredService<BudgetStateLoader>().LoadAsync(Cancellation)).Value;
    }
}
