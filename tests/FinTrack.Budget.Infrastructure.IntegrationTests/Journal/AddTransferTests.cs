using FinTrack.Budget.Application;
using FinTrack.Budget.Application.Buckets;
using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Application.Settings;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;
using FluentResults;
using Microsoft.Extensions.DependencyInjection;
using BucketKind = FinTrack.Budget.Contracts.V1.Buckets.BucketKind;

namespace FinTrack.Budget.Infrastructure.IntegrationTests.Journal;

/// <summary>Transfers through the real ledger and MongoDB: same and different currencies, the balances, rejections that write nothing.</summary>
public sealed class AddTransferTests(BudgetFixture fixture)
{
    // The fixture's clock is FakeClock.DefaultStart: 2026-01-15 in Warsaw.
    private static readonly DateOnly Today = new(2026, 1, 15);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SameAndCrossCurrencyTransfers_GiveTheReplayedBalances()
    {
        var owner = await NewOwnerAsync();
        var cushion = await CreateBucketAsync(owner, "Cushion", Currency.Pln);
        var holiday = await CreateBucketAsync(owner, "Holiday", Currency.Pln);
        var travel = await CreateBucketAsync(owner, "Travel", Currency.Eur);
        await DepositAsync(owner, cushion, 1000m);

        (await TransferAsync(owner, new AddTransfer(Guid.CreateVersion7(), Pln(300m), Pln(299m), Pln(1m), Today, cushion, null, holiday, null, null, []))).IsSuccess.ShouldBeTrue();
        (await TransferAsync(owner, new AddTransfer(Guid.CreateVersion7(), Pln(430m), Money.From(100m, Currency.Eur), null, Today, cushion, null, travel, null, "To euro", []))).IsSuccess.ShouldBeTrue();

        var state = await StateAsync(owner);
        state.StateAsOf(Today).BalanceOf(BucketId.From(cushion)).Available.ShouldBe(Pln(270m));
        state.StateAsOf(Today).BalanceOf(BucketId.From(holiday)).Available.ShouldBe(Pln(299m));
        state.StateAsOf(Today).BalanceOf(BucketId.From(travel)).Available.ShouldBe(Money.From(100m, Currency.Eur));
        state.StateAsOf(Today).BalanceOf(BucketId.From(travel)).Deposited.ShouldBe(Money.Zero(Currency.Eur));
    }

    [Fact]
    public async Task RejectedTransfers_WriteNothing()
    {
        var owner = await NewOwnerAsync();
        var cushion = await CreateBucketAsync(owner, "Cushion", Currency.Pln);
        var holiday = await CreateBucketAsync(owner, "Holiday", Currency.Pln);
        await DepositAsync(owner, cushion, 10m);
        var before = await StateAsync(owner);

        (await TransferAsync(owner, new AddTransfer(Guid.CreateVersion7(), Pln(11m), Pln(11m), null, Today, cushion, null, holiday, null, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketInsufficientFunds, ErrorKind.BusinessRule);
        (await TransferAsync(owner, new AddTransfer(Guid.CreateVersion7(), Pln(5m), Pln(4m), null, Today, cushion, null, holiday, null, null, [])))
            .ShouldFailWith(BudgetErrorCodes.TransferAmountsInconsistent, ErrorKind.Validation);
        (await TransferAsync(owner, new AddTransfer(Guid.CreateVersion7(), Pln(5m), Pln(5m), null, Today, cushion, null, null, Currency.Usd, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketCurrencyMismatch, ErrorKind.BusinessRule);

        var after = await StateAsync(owner);
        after.Version.ShouldBe(before.Version);
        after.Buckets.Count.ShouldBe(before.Buckets.Count);
    }

    private async Task<OwnerId> NewOwnerAsync()
    {
        var owner = BudgetFixture.NewOwner();
        await using var scope = fixture.ScopeFor(owner);
        (await ActivatorUtilities.CreateInstance<SaveOwnerSettingsHandler>(scope.ServiceProvider)
            .Handle(new SaveOwnerSettings(Currency.Pln, "Europe/Warsaw", "pl-PL"), Cancellation)).IsSuccess.ShouldBeTrue();
        return owner;
    }

    private async Task<Guid> CreateBucketAsync(OwnerId owner, string name, Currency currency)
    {
        var id = Guid.CreateVersion7();
        await using var scope = fixture.ScopeFor(owner);
        (await ActivatorUtilities.CreateInstance<CreateBucketHandler>(scope.ServiceProvider)
            .Handle(new CreateBucket(id, name, currency, BucketKind.Savings, null), Cancellation)).IsSuccess.ShouldBeTrue();
        return id;
    }

    private async Task DepositAsync(OwnerId owner, Guid bucket, decimal amount)
    {
        await using var scope = fixture.ScopeFor(owner);
        (await ActivatorUtilities.CreateInstance<AddDepositHandler>(scope.ServiceProvider)
            .Handle(new AddDeposit(Guid.CreateVersion7(), Pln(amount), Today.AddDays(-1), bucket, null, null, []), Cancellation)).IsSuccess.ShouldBeTrue();
    }

    private async Task<Result> TransferAsync(OwnerId owner, AddTransfer command)
    {
        await using var scope = fixture.ScopeFor(owner);
        return await ActivatorUtilities.CreateInstance<AddTransferHandler>(scope.ServiceProvider).Handle(command, Cancellation);
    }

    private async Task<BudgetSnapshot> StateAsync(OwnerId owner)
    {
        await using var scope = fixture.ScopeFor(owner);
        return (await scope.ServiceProvider.GetRequiredService<BudgetStateLoader>().LoadAsync(Cancellation)).Value;
    }

    private static Money Pln(decimal amount) => Money.From(amount, Currency.Pln);
}
