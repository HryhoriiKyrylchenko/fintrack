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
using InvestmentProfile = FinTrack.Budget.Contracts.V1.Buckets.InvestmentProfile;

namespace FinTrack.Budget.Infrastructure.IntegrationTests.Journal;

/// <summary>Opening balances and spends through the real ledger and MongoDB: balances, the rules, and nothing written on a rejection.</summary>
public sealed class OpeningBalanceAndSpendTests(BudgetFixture fixture)
{
    // The fixture's clock is FakeClock.DefaultStart: 2026-01-15 in Warsaw.
    private static readonly DateOnly Today = new(2026, 1, 15);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OpeningBalanceThenSpends_GiveTheReplayedBalance()
    {
        var owner = await NewOwnerAsync();
        var bucket = await CreateBucketAsync(owner, BucketKind.Savings);

        (await SendAsync(owner, new AddOpeningBalance(Guid.CreateVersion7(), Money.From(1000m, Currency.Pln), Today.AddDays(-100), bucket, null, null, []))).IsSuccess.ShouldBeTrue();
        (await SendAsync(owner, new AddSpend(Guid.CreateVersion7(), Money.From(250.75m, Currency.Pln), Today.AddDays(-50), bucket, null, "Car repair", ["car"]))).IsSuccess.ShouldBeTrue();
        (await SendAsync(owner, new AddSpend(Guid.CreateVersion7(), Money.From(749.25m, Currency.Pln), Today, bucket, null, null, []))).IsSuccess.ShouldBeTrue();

        var balance = (await StateAsync(owner)).State.BalanceOf(BucketId.From(bucket));
        balance.Available.ShouldBe(Money.Zero(Currency.Pln));
        balance.Deposited.ShouldBe(Money.Zero(Currency.Pln));
    }

    [Fact]
    public async Task BackdatedSpend_ThatWouldOverdrawAPastDay_IsRejected_AndWritesNothing()
    {
        var owner = await NewOwnerAsync();
        var bucket = await CreateBucketAsync(owner, BucketKind.Savings);
        await SendAsync(owner, new AddDeposit(Guid.CreateVersion7(), Money.From(100m, Currency.Pln), Today.AddDays(-10), bucket, null, null, []));
        var before = await StateAsync(owner);

        var error = (await SendAsync(owner, new AddSpend(Guid.CreateVersion7(), Money.From(60m, Currency.Pln), Today.AddDays(-30), bucket, null, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketInsufficientFunds, ErrorKind.BusinessRule);

        error.Parameters["date"].ShouldBe("2025-12-16");
        var after = await StateAsync(owner);
        after.Version.ShouldBe(before.Version);
        after.Journal.Count.ShouldBe(before.Journal.Count);
    }

    [Fact]
    public async Task SecondOpeningBalance_IsRejected()
    {
        var owner = await NewOwnerAsync();
        var bucket = await CreateBucketAsync(owner, BucketKind.Savings);
        await SendAsync(owner, new AddOpeningBalance(Guid.CreateVersion7(), Money.From(10m, Currency.Pln), Today.AddDays(-5), bucket, null, null, []));

        (await SendAsync(owner, new AddOpeningBalance(Guid.CreateVersion7(), Money.From(20m, Currency.Pln), Today.AddDays(-4), bucket, null, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketOpeningBalanceExists, ErrorKind.BusinessRule);
    }

    [Fact]
    public async Task InvestmentBucket_AcceptsAnOpeningBalance_ButNoSpend()
    {
        var owner = await NewOwnerAsync();
        var bucket = await CreateBucketAsync(owner, BucketKind.Investment);

        (await SendAsync(owner, new AddOpeningBalance(Guid.CreateVersion7(), Money.From(10m, Currency.Pln), Today.AddDays(-5), bucket, null, null, []))).IsSuccess.ShouldBeTrue();
        (await SendAsync(owner, new AddSpend(Guid.CreateVersion7(), Money.From(1m, Currency.Pln), Today, bucket, null, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketSpendNotAllowed, ErrorKind.BusinessRule);
    }

    private async Task<OwnerId> NewOwnerAsync()
    {
        var owner = BudgetFixture.NewOwner();
        await using var scope = fixture.ScopeFor(owner);
        (await ActivatorUtilities.CreateInstance<SaveOwnerSettingsHandler>(scope.ServiceProvider)
            .Handle(new SaveOwnerSettings(Currency.Pln, "Europe/Warsaw", "pl-PL"), Cancellation)).IsSuccess.ShouldBeTrue();
        return owner;
    }

    private async Task<Guid> CreateBucketAsync(OwnerId owner, BucketKind kind)
    {
        var id = Guid.CreateVersion7();
        await using var scope = fixture.ScopeFor(owner);
        (await ActivatorUtilities.CreateInstance<CreateBucketHandler>(scope.ServiceProvider)
            .Handle(new CreateBucket(id, kind == BucketKind.Savings ? "Cushion" : "Stocks", Currency.Pln, kind, kind == BucketKind.Savings ? null : InvestmentProfile.RealValueAssets), Cancellation))
            .IsSuccess.ShouldBeTrue();
        return id;
    }

    private async Task<Result> SendAsync(OwnerId owner, AddOpeningBalance command)
    {
        await using var scope = fixture.ScopeFor(owner);
        return await ActivatorUtilities.CreateInstance<AddOpeningBalanceHandler>(scope.ServiceProvider).Handle(command, Cancellation);
    }

    private async Task<Result> SendAsync(OwnerId owner, AddDeposit command)
    {
        await using var scope = fixture.ScopeFor(owner);
        return await ActivatorUtilities.CreateInstance<AddDepositHandler>(scope.ServiceProvider).Handle(command, Cancellation);
    }

    private async Task<Result> SendAsync(OwnerId owner, AddSpend command)
    {
        await using var scope = fixture.ScopeFor(owner);
        return await ActivatorUtilities.CreateInstance<AddSpendHandler>(scope.ServiceProvider).Handle(command, Cancellation);
    }

    private async Task<BudgetSnapshot> StateAsync(OwnerId owner)
    {
        await using var scope = fixture.ScopeFor(owner);
        return await scope.ServiceProvider.GetRequiredService<BudgetStateLoader>().LoadAsync(Cancellation);
    }
}
