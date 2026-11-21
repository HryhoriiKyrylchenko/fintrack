using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Domain.Tests.State;

/// <summary>
/// Adjustments in the state builder (ARCHITECTURE 4.3, plan 1b B14): an increase raises <c>NetContributed</c>, a
/// decrease lowers it; neither is income (invariant 5); both are tracked on their own; a decrease of a savings
/// bucket is held to invariant 1 at every point of the replay order, an investment bucket may go below zero (4.9).
/// </summary>
public class AdjustmentStateTests
{
    private static readonly OwnerId Owner = OwnerId.From(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly DateOnly Today = new(2026, 1, 15);
    private static readonly EntryInfo Reason = new("Counted the cash box", [], EntrySource.Manual);

    private readonly Bucket cushion = Bucket.Create(BucketId.From(Guid.CreateVersion7()), Owner, "Cushion", Currency.Pln, BucketKind.Savings, null, []).Value;
    private readonly Bucket crypto = Bucket.Create(BucketId.From(Guid.CreateVersion7()), Owner, "Crypto", Currency.Pln, BucketKind.Investment, InvestmentProfile.Crypto, []).Value;
    private int recorded;

    [Fact]
    public void WithoutAdjustments_BothTotalsAreZero()
    {
        var balance = Replay(Deposit(cushion, 10m, -1)).BalanceOf(cushion.BucketId);

        balance.AdjustedIn.ShouldBe(Money.Zero(Currency.Pln));
        balance.AdjustedOut.ShouldBe(Money.Zero(Currency.Pln));
    }

    [Fact]
    public void AnIncrease_RaisesNetContributed_ButIsNotIncome()
    {
        var balance = Replay(Deposit(cushion, 100m, -10), Adjust(cushion, 12.34m, AdjustmentDirection.Increase, -5)).BalanceOf(cushion.BucketId);

        balance.NetContributed.ShouldBe(Money.From(112.34m, Currency.Pln));
        balance.Available.ShouldBe(Money.From(112.34m, Currency.Pln));
        balance.Deposited.ShouldBe(Money.From(100m, Currency.Pln));
        balance.AdjustedIn.ShouldBe(Money.From(12.34m, Currency.Pln));
        balance.AdjustedOut.ShouldBe(Money.Zero(Currency.Pln));
        balance.HasOpeningBalance.ShouldBeFalse();
    }

    [Fact]
    public void ADecrease_LowersNetContributed_AndLeavesIncomeAlone()
    {
        var balance = Replay(Deposit(cushion, 100m, -10), Adjust(cushion, 30.50m, AdjustmentDirection.Decrease, -5)).BalanceOf(cushion.BucketId);

        balance.NetContributed.ShouldBe(Money.From(69.50m, Currency.Pln));
        balance.Available.ShouldBe(Money.From(69.50m, Currency.Pln));
        balance.Deposited.ShouldBe(Money.From(100m, Currency.Pln));
        balance.AdjustedIn.ShouldBe(Money.Zero(Currency.Pln));
        balance.AdjustedOut.ShouldBe(Money.From(30.50m, Currency.Pln));
    }

    [Fact]
    public void BothDirections_AreSummedSeparately()
    {
        var balance = Replay(
                Adjust(cushion, 50m, AdjustmentDirection.Increase, -9),
                Adjust(cushion, 20m, AdjustmentDirection.Decrease, -8),
                Adjust(cushion, 5m, AdjustmentDirection.Increase, -7),
                Adjust(cushion, 1m, AdjustmentDirection.Decrease, -6))
            .BalanceOf(cushion.BucketId);

        balance.NetContributed.ShouldBe(Money.From(34m, Currency.Pln));
        balance.AdjustedIn.ShouldBe(Money.From(55m, Currency.Pln));
        balance.AdjustedOut.ShouldBe(Money.From(21m, Currency.Pln));
        balance.Deposited.ShouldBe(Money.Zero(Currency.Pln));
    }

    [Fact]
    public void ADecreaseToExactlyZero_IsAllowed()
    {
        var state = Replay(Deposit(cushion, 100m, -10), Adjust(cushion, 100m, AdjustmentDirection.Decrease, -5));

        state.BalanceOf(cushion.BucketId).Available.ShouldBe(Money.Zero(Currency.Pln));
        state.FirstOverdraft.ShouldBeNull();
    }

    [Fact]
    public void ADecreaseBeforeTheMoneyArrived_IsAnOverdraftOnThatDay()
    {
        // Today the bucket holds 60, but on day -10 it held -40.
        var state = Replay(Deposit(cushion, 100m, -5), Adjust(cushion, 40m, AdjustmentDirection.Decrease, -10));

        state.BalanceOf(cushion.BucketId).Available.ShouldBe(Money.From(60m, Currency.Pln));
        state.FirstOverdraft.ShouldBe(new Overdraft(cushion.BucketId, Today.AddDays(-10)));
    }

    [Fact]
    public void AnIncrease_CanFundALaterSpend()
    {
        var state = Replay(Adjust(cushion, 10m, AdjustmentDirection.Increase, -3), Spend(cushion, 10m, -2));

        state.FirstOverdraft.ShouldBeNull();
        state.BalanceOf(cushion.BucketId).NetContributed.ShouldBe(Money.Zero(Currency.Pln));
    }

    [Fact]
    public void AnInvestmentBucket_MayBeAdjustedBelowZero_WithoutAnOverdraft()
    {
        var state = Replay(Adjust(crypto, 10m, AdjustmentDirection.Decrease, -1));

        state.BalanceOf(crypto.BucketId).NetContributed.ShouldBe(-Money.From(10m, Currency.Pln));
        state.BalanceOf(crypto.BucketId).AdjustedOut.ShouldBe(Money.From(10m, Currency.Pln));
        state.FirstOverdraft.ShouldBeNull();
    }

    [Fact]
    public void AReversedAdjustment_CountsAsIfItNeverHappened()
    {
        var decrease = Adjust(cushion, 30m, AdjustmentDirection.Decrease, -5);
        var reversal = Reversal.Create(NewId(), Owner, JournalEntryId.From(decrease.Id), Today, Today, EntryInfo.None, NextRecordedAt()).Value;

        var balance = Replay(Deposit(cushion, 100m, -10), decrease, reversal).BalanceOf(cushion.BucketId);

        balance.NetContributed.ShouldBe(Money.From(100m, Currency.Pln));
        balance.AdjustedOut.ShouldBe(Money.Zero(Currency.Pln));
    }

    private BudgetState Replay(params JournalEntry[] journal) => BudgetState.Replay([cushion, crypto], journal, Today);

    private JournalEntry Adjust(Bucket bucket, decimal amount, AdjustmentDirection direction, int day) =>
        Adjustment.Create(NewId(), Owner, bucket.BucketId, Money.From(amount, bucket.Currency), direction, Today.AddDays(day), Today, Reason, NextRecordedAt()).Value;

    private JournalEntry Deposit(Bucket bucket, decimal amount, int day) =>
        Domain.Journal.Deposit.Create(NewId(), Owner, bucket.BucketId, Money.From(amount, bucket.Currency), Today.AddDays(day), Today, EntryInfo.None, NextRecordedAt()).Value;

    private JournalEntry Spend(Bucket bucket, decimal amount, int day) =>
        Domain.Journal.Spend.Create(NewId(), Owner, bucket.BucketId, Money.From(amount, bucket.Currency), Today.AddDays(day), Today, EntryInfo.None, NextRecordedAt()).Value;

    // Entries are recorded one after another, as a user enters them.
    private DateTimeOffset NextRecordedAt() => FakeClock.DefaultStart.AddSeconds(recorded++);

    private static JournalEntryId NewId() => JournalEntryId.From(Guid.CreateVersion7());
}
