using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Domain.Tests.State;

/// <summary>
/// The state builder (ARCHITECTURE 2.3, 4.3): balances come only from replaying the journal in the
/// replay order (<c>OccurredOn</c>, <c>RecordedAt</c>, <c>Id</c>).
/// </summary>
public class BudgetStateTests
{
    private static readonly OwnerId Owner = OwnerId.From(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly DateOnly Today = new(2026, 1, 15);

    [Fact]
    public void WithoutEntries_EveryBucketIsZero()
    {
        var bucket = Savings("Cushion", Currency.Pln);

        var balance = BudgetState.Replay([bucket], []).BalanceOf(bucket.BucketId);

        balance.NetContributed.ShouldBe(Money.Zero(Currency.Pln));
        balance.OutstandingPrincipal.ShouldBe(Money.Zero(Currency.Pln));
        balance.Available.ShouldBe(Money.Zero(Currency.Pln));
        balance.Deposited.ShouldBe(Money.Zero(Currency.Pln));
    }

    [Fact]
    public void Deposits_RaiseNetContributedAndAvailable()
    {
        var cushion = Savings("Cushion", Currency.Pln);
        var holiday = Savings("Holiday", Currency.Eur);

        var state = BudgetState.Replay(
            [cushion, holiday],
            [Deposit(cushion, 100.50m, Currency.Pln), Deposit(cushion, 20m, Currency.Pln), Deposit(holiday, 5m, Currency.Eur)]);

        state.BalanceOf(cushion.BucketId).NetContributed.ShouldBe(Money.From(120.50m, Currency.Pln));
        state.BalanceOf(cushion.BucketId).Available.ShouldBe(Money.From(120.50m, Currency.Pln));
        state.BalanceOf(cushion.BucketId).Deposited.ShouldBe(Money.From(120.50m, Currency.Pln));
        state.BalanceOf(holiday.BucketId).Available.ShouldBe(Money.From(5m, Currency.Eur));
    }

    [Fact]
    public void BalancesAreInTheBucketsCurrency_NeverMixed()
    {
        var bucket = Savings("Cushion", Currency.Pln);

        Should.Throw<CurrencyMismatchException>(() => BudgetState.Replay([bucket], [Deposit(bucket, 1m, Currency.Eur)]));
    }

    [Fact]
    public void EntryOfAnUnknownBucket_IsABug()
    {
        var known = Savings("Cushion", Currency.Pln);
        var unknown = Savings("Elsewhere", Currency.Pln);

        Should.Throw<InvalidOperationException>(() => BudgetState.Replay([known], [Deposit(unknown, 1m, Currency.Pln)]));
    }

    [Fact]
    public void Apply_GivesANewState_AndLeavesTheOldOneUnchanged()
    {
        var bucket = Savings("Cushion", Currency.Pln);
        var empty = BudgetState.Replay([bucket], []);

        var after = empty.Apply(Deposit(bucket, 7m, Currency.Pln));

        after.BalanceOf(bucket.BucketId).Available.ShouldBe(Money.From(7m, Currency.Pln));
        empty.BalanceOf(bucket.BucketId).Available.ShouldBe(Money.Zero(Currency.Pln));
    }

    [Fact]
    public void Balances_CoverEveryBucket_IncludingArchivedAndSystemOnes()
    {
        var unallocated = Bucket.Unallocated(BucketId.From(Guid.CreateVersion7()), Owner, Currency.Usd);

        var state = BudgetState.Replay([unallocated], [Deposit(unallocated, 3m, Currency.Usd)]);

        state.Balances.ShouldHaveSingleItem().Available.ShouldBe(Money.From(3m, Currency.Usd));
    }

    [Fact]
    public void ReplayOrder_IsDateThenRecordedAtThenId()
    {
        var bucket = Savings("Cushion", Currency.Pln);
        var late = Deposit(bucket, 1m, Currency.Pln, occurredOn: Today, recordedAt: FakeClock.DefaultStart);
        var early = Deposit(bucket, 1m, Currency.Pln, occurredOn: Today.AddDays(-5), recordedAt: FakeClock.DefaultStart.AddDays(1));
        var sameDayLater = Deposit(bucket, 1m, Currency.Pln, occurredOn: Today, recordedAt: FakeClock.DefaultStart.AddSeconds(1));

        ReplayOrder.Sort([late, sameDayLater, early]).ShouldBe([early, late, sameDayLater]);
    }

    [Fact]
    public void ReplayOrder_BreaksTiesByTheIdsBytes_AsMongoDbSortsThem()
    {
        var bucket = Savings("Cushion", Currency.Pln);
        var low = Deposit(bucket, 1m, Currency.Pln, id: Guid.Parse("00000000-0000-7000-8000-0000000000ff"));
        var high = Deposit(bucket, 1m, Currency.Pln, id: Guid.Parse("00000001-0000-7000-8000-000000000000"));
        var middle = Deposit(bucket, 1m, Currency.Pln, id: Guid.Parse("00000000-0001-7000-8000-000000000000"));

        ReplayOrder.Sort([high, middle, low]).ShouldBe([low, middle, high]);
    }

    [Theory]
    // Big-endian: the first byte counts most. Little-endian (Guid.ToByteArray()) would reverse these.
    [InlineData("00000001-0000-7000-8000-000000000000", "01000000-0000-7000-8000-000000000000")]
    [InlineData("00000000-0001-7000-8000-000000000000", "00000000-0100-7000-8000-000000000000")]
    [InlineData("7fffffff-0000-7000-8000-000000000000", "80000000-0000-7000-8000-000000000000")]
    [InlineData("00000000-0000-7000-8000-0000000000ff", "00000000-0000-7000-8001-000000000000")]
    public void Ids_CompareByTheirStandardBytes(string lower, string higher)
    {
        ReplayOrder.CompareIds(Guid.Parse(lower), Guid.Parse(higher)).ShouldBeLessThan(0);
        ReplayOrder.CompareIds(Guid.Parse(higher), Guid.Parse(lower)).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Available_IsNetContributed_LessOutstandingPrincipal()
    {
        // ARCHITECTURE 4.3's example: 1,000,000 contributed, 100,000 borrowed and not yet returned. Until loans
        // exist (phase 1b) no replay produces a principal, so the formula (invariant 2) is pinned here directly.
        var balance = new BucketBalance(
            BucketId.From(Guid.CreateVersion7()), BucketKind.Savings, Money.From(1_000_000m, Currency.Pln), Money.From(100_000m, Currency.Pln),
            Money.Zero(Currency.Pln), HasOpeningBalance: false, FirstNegativeOn: null);

        balance.Available.ShouldBe(Money.From(900_000m, Currency.Pln));
    }

    private static Bucket Savings(string name, Currency currency) =>
        Bucket.Create(BucketId.From(Guid.CreateVersion7()), Owner, name, currency, BucketKind.Savings, null, []).Value;

    private static Deposit Deposit(
        Bucket bucket,
        decimal amount,
        Currency currency,
        DateOnly? occurredOn = null,
        DateTimeOffset? recordedAt = null,
        Guid? id = null) =>
        Domain.Journal.Deposit.Create(
            JournalEntryId.From(id ?? Guid.CreateVersion7()),
            Owner,
            bucket.BucketId,
            Money.From(amount, currency),
            occurredOn ?? Today,
            Today,
            EntryInfo.None,
            recordedAt ?? FakeClock.DefaultStart).Value;
}
