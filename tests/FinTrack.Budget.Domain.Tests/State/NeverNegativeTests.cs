using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Domain.Tests.State;

/// <summary>
/// Opening balances and spends in the state builder, and invariant 1 as D2 states it: a savings
/// bucket's <c>Available</c> is never below zero at any point of the replay order, not only today.
/// </summary>
public class NeverNegativeTests
{
    private static readonly OwnerId Owner = OwnerId.From(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly DateOnly Today = new(2026, 1, 15);

    private readonly Bucket cushion = Bucket.Create(BucketId.From(Guid.CreateVersion7()), Owner, "Cushion", Currency.Pln, BucketKind.Savings, null, []).Value;
    private readonly Bucket crypto = Bucket.Create(BucketId.From(Guid.CreateVersion7()), Owner, "Crypto", Currency.Pln, BucketKind.Investment, InvestmentProfile.Crypto, []).Value;
    private int recorded;

    [Fact]
    public void OpeningBalance_RaisesNetContributed_ButIsNotIncome()
    {
        var balance = Replay(Opening(cushion, 1000m, -100)).BalanceOf(cushion.BucketId);

        balance.NetContributed.ShouldBe(Money.From(1000m, Currency.Pln));
        balance.Available.ShouldBe(Money.From(1000m, Currency.Pln));
        balance.Deposited.ShouldBe(Money.Zero(Currency.Pln));
        balance.HasOpeningBalance.ShouldBeTrue();
    }

    [Fact]
    public void Spend_LowersNetContributedAndAvailable()
    {
        var balance = Replay(Deposit(cushion, 100m, -10), Spend(cushion, 30.50m, -5)).BalanceOf(cushion.BucketId);

        balance.NetContributed.ShouldBe(Money.From(69.50m, Currency.Pln));
        balance.Available.ShouldBe(Money.From(69.50m, Currency.Pln));
        balance.Deposited.ShouldBe(Money.From(100m, Currency.Pln));
        balance.FirstNegativeOn.ShouldBeNull();
    }

    [Fact]
    public void SpendingEverything_ReachesZero_WhichIsAllowed()
    {
        var state = Replay(Deposit(cushion, 100m, -10), Spend(cushion, 100m, -5));

        state.BalanceOf(cushion.BucketId).Available.ShouldBe(Money.Zero(Currency.Pln));
        state.FirstOverdraft.ShouldBeNull();
    }

    [Fact]
    public void SpendBeforeTheMoneyArrived_IsAnOverdraftOnThatDay_EvenIfTodayIsFine()
    {
        // Today the bucket holds 50, but on day -10 it held -50.
        var state = Replay(Deposit(cushion, 100m, -5), Spend(cushion, 50m, -10));

        state.BalanceOf(cushion.BucketId).Available.ShouldBe(Money.From(50m, Currency.Pln));
        state.FirstOverdraft.ShouldBe(new Overdraft(cushion.BucketId, Today.AddDays(-10)));
    }

    [Fact]
    public void OnOneDay_TheOrderIsWhenEntriesWereRecorded()
    {
        // Same date: the spend was recorded before the deposit, so for a moment the bucket was negative.
        var state = Replay(Spend(cushion, 10m, -1), Deposit(cushion, 100m, -1));

        state.FirstOverdraft.ShouldBe(new Overdraft(cushion.BucketId, Today.AddDays(-1)));
    }

    [Fact]
    public void Overdraft_RemembersTheFirstDay_AlsoWhenTheBalanceRecovers()
    {
        var state = Replay(Spend(cushion, 10m, -20), Deposit(cushion, 100m, -15), Spend(cushion, 200m, -3));

        state.FirstOverdraft!.Date.ShouldBe(Today.AddDays(-20));
    }

    [Fact]
    public void InvestmentBuckets_MayGoBelowZero_WithoutAnOverdraft()
    {
        // Not reachable through Spend (rejected for investment buckets), but through transfers out (step 8).
        var state = Replay(Spend(crypto, 10m, -1));

        state.BalanceOf(crypto.BucketId).Available.ShouldBe(-Money.From(10m, Currency.Pln));
        state.FirstOverdraft.ShouldBeNull();
    }

    [Fact]
    public void FirstOverdraft_IsTheEarliestDay_AcrossBuckets()
    {
        var holiday = Bucket.Create(BucketId.From(Guid.CreateVersion7()), Owner, "Holiday", Currency.Pln, BucketKind.Savings, null, []).Value;

        var state = BudgetState.Replay([cushion, holiday], [Spend(cushion, 1m, -2), Spend(holiday, 1m, -7)]);

        state.FirstOverdraft.ShouldBe(new Overdraft(holiday.BucketId, Today.AddDays(-7)));
    }

    [Fact]
    public void WithoutAnOpeningBalance_HasOpeningBalanceIsFalse() =>
        Replay(Deposit(cushion, 1m, 0)).BalanceOf(cushion.BucketId).HasOpeningBalance.ShouldBeFalse();

    private BudgetState Replay(params JournalEntry[] journal) => BudgetState.Replay([cushion, crypto], journal);

    private JournalEntry Opening(Bucket bucket, decimal amount, int day) =>
        OpeningBalance.Create(NewId(), Owner, bucket.BucketId, Money.From(amount, bucket.Currency), Today.AddDays(day), Today, EntryInfo.None, NextRecordedAt()).Value;

    private JournalEntry Deposit(Bucket bucket, decimal amount, int day) =>
        Domain.Journal.Deposit.Create(NewId(), Owner, bucket.BucketId, Money.From(amount, bucket.Currency), Today.AddDays(day), Today, EntryInfo.None, NextRecordedAt()).Value;

    private JournalEntry Spend(Bucket bucket, decimal amount, int day) =>
        Domain.Journal.Spend.Create(NewId(), Owner, bucket.BucketId, Money.From(amount, bucket.Currency), Today.AddDays(day), Today, EntryInfo.None, NextRecordedAt()).Value;

    // Entries are recorded one after another, as a user enters them.
    private DateTimeOffset NextRecordedAt() => FakeClock.DefaultStart.AddSeconds(recorded++);

    private static JournalEntryId NewId() => JournalEntryId.From(Guid.CreateVersion7());
}
