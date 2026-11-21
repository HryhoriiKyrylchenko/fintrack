using FinTrack.Budget.Application;
using FinTrack.Budget.Application.Buckets;
using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Application.Settings;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;
using FluentResults;
using Microsoft.Extensions.DependencyInjection;
using BucketKind = FinTrack.Budget.Contracts.V1.Buckets.BucketKind;
using Contract = FinTrack.Budget.Contracts.V1.Journal;

namespace FinTrack.Budget.Infrastructure.IntegrationTests.Journal;

/// <summary>
/// Adjustments (plan 1b B14) through the real ledger and MongoDB: both directions, a decrease below zero refused with the
/// day and nothing written, a repeat writing nothing, and a reversal.
/// </summary>
public sealed class AddAdjustmentTests(BudgetFixture fixture)
{
    // The fixture's clock is FakeClock.DefaultStart: 2026-01-15 in Warsaw.
    private static readonly DateOnly Today = new(2026, 1, 15);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnIncreaseAndADecrease_GiveTheReplayedBalance_WithoutIncome()
    {
        var owner = await NewOwnerAsync();
        var bucket = await CreateBucketAsync(owner);
        await DepositAsync(owner, bucket, 100m, -10);

        (await SendAsync(owner, Adjust(bucket, Contract.AdjustmentDirection.Increase, 20.50m, -5))).IsSuccess.ShouldBeTrue();
        (await SendAsync(owner, Adjust(bucket, Contract.AdjustmentDirection.Decrease, 0.50m, -1))).IsSuccess.ShouldBeTrue();

        var state = await StateAsync(owner);
        var balance = state.StateAsOf(Today).BalanceOf(BucketId.From(bucket));
        balance.NetContributed.ShouldBe(Money.From(120m, Currency.Pln));
        balance.Deposited.ShouldBe(Money.From(100m, Currency.Pln));
        balance.AdjustedIn.ShouldBe(Money.From(20.50m, Currency.Pln));
        balance.AdjustedOut.ShouldBe(Money.From(0.50m, Currency.Pln));
        state.Journal.OfType<Adjustment>().Select(adjustment => adjustment.Direction).ShouldBe([AdjustmentDirection.Increase, AdjustmentDirection.Decrease], ignoreOrder: true);
    }

    [Fact]
    public async Task ADecreaseBelowZeroOnAPastDay_IsRejected_AndWritesNothing()
    {
        var owner = await NewOwnerAsync();
        var bucket = await CreateBucketAsync(owner);
        await DepositAsync(owner, bucket, 100m, -10);
        var before = await StateAsync(owner);

        var error = (await SendAsync(owner, Adjust(bucket, Contract.AdjustmentDirection.Decrease, 1m, -30)))
            .ShouldFailWith(BudgetErrorCodes.BucketInsufficientFunds, ErrorKind.BusinessRule);

        error.Parameters["date"].ShouldBe("2025-12-16");
        var after = await StateAsync(owner);
        after.Version.ShouldBe(before.Version);
        after.Journal.Count.ShouldBe(before.Journal.Count);
    }

    [Fact]
    public async Task ARepeat_WritesNothing_AndTheAdjustmentCanBeReversed()
    {
        var owner = await NewOwnerAsync();
        var bucket = await CreateBucketAsync(owner);
        var increase = Adjust(bucket, Contract.AdjustmentDirection.Increase, 40m, -2);
        (await SendAsync(owner, increase)).IsSuccess.ShouldBeTrue();
        var written = await StateAsync(owner);

        (await SendAsync(owner, increase)).IsSuccess.ShouldBeTrue();
        (await StateAsync(owner)).Version.ShouldBe(written.Version);

        await using (var scope = fixture.ScopeFor(owner))
        {
            (await ActivatorUtilities.CreateInstance<ReverseEntryHandler>(scope.ServiceProvider)
                .Handle(new ReverseEntry(Guid.CreateVersion7(), increase.Id, Today, null, []), Cancellation)).IsSuccess.ShouldBeTrue();
        }

        var balance = (await StateAsync(owner)).StateAsOf(Today).BalanceOf(BucketId.From(bucket));
        balance.NetContributed.ShouldBe(Money.Zero(Currency.Pln));
        balance.AdjustedIn.ShouldBe(Money.Zero(Currency.Pln));
    }

    private static AddAdjustment Adjust(Guid bucket, Contract.AdjustmentDirection direction, decimal amount, int day) =>
        new(Guid.CreateVersion7(), Money.From(amount, Currency.Pln), direction, Today.AddDays(day), bucket, null, "Counted the cash box", []);

    private async Task<OwnerId> NewOwnerAsync()
    {
        var owner = BudgetFixture.NewOwner();
        await using var scope = fixture.ScopeFor(owner);
        (await ActivatorUtilities.CreateInstance<SaveOwnerSettingsHandler>(scope.ServiceProvider)
            .Handle(new SaveOwnerSettings(Currency.Pln, "Europe/Warsaw", "pl-PL"), Cancellation)).IsSuccess.ShouldBeTrue();
        return owner;
    }

    private async Task<Guid> CreateBucketAsync(OwnerId owner)
    {
        var id = Guid.CreateVersion7();
        await using var scope = fixture.ScopeFor(owner);
        (await ActivatorUtilities.CreateInstance<CreateBucketHandler>(scope.ServiceProvider)
            .Handle(new CreateBucket(id, "Cushion", Currency.Pln, BucketKind.Savings, null), Cancellation)).IsSuccess.ShouldBeTrue();
        return id;
    }

    private async Task DepositAsync(OwnerId owner, Guid bucket, decimal amount, int day)
    {
        await using var scope = fixture.ScopeFor(owner);
        (await ActivatorUtilities.CreateInstance<AddDepositHandler>(scope.ServiceProvider)
            .Handle(new AddDeposit(Guid.CreateVersion7(), Money.From(amount, Currency.Pln), Today.AddDays(day), bucket, null, null, []), Cancellation)).IsSuccess.ShouldBeTrue();
    }

    private async Task<Result> SendAsync(OwnerId owner, AddAdjustment command)
    {
        await using var scope = fixture.ScopeFor(owner);
        return await ActivatorUtilities.CreateInstance<AddAdjustmentHandler>(scope.ServiceProvider).Handle(command, Cancellation);
    }

    private async Task<BudgetSnapshot> StateAsync(OwnerId owner)
    {
        await using var scope = fixture.ScopeFor(owner);
        return (await scope.ServiceProvider.GetRequiredService<BudgetStateLoader>().LoadAsync(Cancellation)).Value;
    }
}
