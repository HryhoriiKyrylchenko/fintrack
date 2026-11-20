using CsCheck;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Domain.Tests.Journal;

/// <summary>
/// A reversal (ARCHITECTURE 4.2, plan 1a D3) cancels one earlier entry: the state builder then treats
/// that entry as if it never happened, at its original date. The reversal itself moves no money.
/// </summary>
public class ReversalTests
{
    private static readonly OwnerId Owner = OwnerId.From(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly DateOnly Today = new(2026, 1, 15);

    private readonly Bucket cushion = Bucket.Create(BucketId.From(Guid.CreateVersion7()), Owner, "Cushion", Currency.Pln, BucketKind.Savings, null, []).Value;
    private readonly Bucket holiday = Bucket.Create(BucketId.From(Guid.CreateVersion7()), Owner, "Holiday", Currency.Pln, BucketKind.Savings, null, []).Value;
    private int recorded;

    [Fact]
    public void Create_ReferencesTheReversedEntry()
    {
        var target = JournalEntryId.From(Guid.CreateVersion7());

        var reversal = Reversal.Create(JournalEntryId.From(Guid.CreateVersion7()), Owner, target, Today, Today, EntryInfo.None, FakeClock.DefaultStart).Value;

        reversal.ReversedEntryId.ShouldBe(target);
        reversal.OccurredOn.ShouldBe(Today);
    }

    [Fact]
    public void Create_DatedAfterToday_IsRejected() =>
        Reversal.Create(JournalEntryId.From(Guid.CreateVersion7()), Owner, JournalEntryId.From(Guid.CreateVersion7()), Today.AddDays(1), Today, EntryInfo.None, FakeClock.DefaultStart)
            .ShouldFailWith(BudgetErrorCodes.EntryDateInFuture, ErrorKind.BusinessRule);

    [Fact]
    public void ReversedEntry_CountsAsIfItNeverHappened()
    {
        var deposit = Deposit(cushion, 100m, -10);
        var spend = Spend(cushion, 30m, -5);

        var state = Replay(deposit, spend, Reverse(spend));

        state.BalanceOf(cushion.BucketId).Available.ShouldBe(Money.From(100m, Currency.Pln));
    }

    [Fact]
    public void ReversingATransfer_VoidsBothSides()
    {
        var deposit = Deposit(cushion, 100m, -10);
        var transfer = Transfer.Create(NewId(), Owner, cushion.BucketId, holiday.BucketId, Money.From(40m, Currency.Pln), Money.From(40m, Currency.Pln), null,
            Today.AddDays(-5), Today, EntryInfo.None, NextRecordedAt()).Value;

        var state = Replay(deposit, transfer, Reverse(transfer));

        state.BalanceOf(cushion.BucketId).Available.ShouldBe(Money.From(100m, Currency.Pln));
        state.BalanceOf(holiday.BucketId).Available.ShouldBe(Money.Zero(Currency.Pln));
    }

    [Fact]
    public void ReversingADepositThatWasSpent_LeavesAnOverdraft_AtTheSpendsDay()
    {
        var deposit = Deposit(cushion, 100m, -10);
        var spend = Spend(cushion, 60m, -5);

        Replay(deposit, spend, Reverse(deposit)).FirstOverdraft.ShouldBe(new Overdraft(cushion.BucketId, Today.AddDays(-5)));
    }

    [Fact]
    public void ReversedOpeningBalance_NoLongerCounts()
    {
        var opening = OpeningBalance.Create(NewId(), Owner, cushion.BucketId, Money.From(50m, Currency.Pln), Today.AddDays(-30), Today, EntryInfo.None, NextRecordedAt()).Value;

        var balance = Replay(opening, Reverse(opening)).BalanceOf(cushion.BucketId);

        balance.HasOpeningBalance.ShouldBeFalse();
        balance.Available.ShouldBe(Money.Zero(Currency.Pln));
    }

    [Fact]
    public void ReversedEntryIds_ListsWhatIsCancelled()
    {
        var deposit = Deposit(cushion, 1m, -1);
        var reversal = Reverse(deposit);

        BudgetState.ReversedEntryIds([deposit, reversal]).ShouldBe([deposit.Id]);
    }

    [Fact]
    public void HasSameContent_ComparesTheReversedEntry()
    {
        var id = NewId();
        var target = JournalEntryId.From(Guid.CreateVersion7());
        var first = Reversal.Create(id, Owner, target, Today, Today, EntryInfo.None, FakeClock.DefaultStart).Value;

        first.HasSameContent(Reversal.Create(id, Owner, target, Today, Today, EntryInfo.None, FakeClock.DefaultStart.AddHours(1)).Value).ShouldBeTrue();
        first.HasSameContent(Reversal.Create(id, Owner, JournalEntryId.From(Guid.CreateVersion7()), Today, Today, EntryInfo.None, FakeClock.DefaultStart).Value).ShouldBeFalse();
    }

    // "Reversing restores the earlier balances exactly": a journal with a reversal of one entry
    // replays to the same state as the journal without that entry.
    [Fact]
    public void ReversingAnyEntry_EqualsTheJournalWithoutIt() =>
        Gen.Select(Gen.Int[1, 30], Gen.Int[0, 1000], Gen.Int)
            .Sample((count, pick, seed) =>
            {
                var random = new Random(seed);
                var journal = Enumerable.Range(0, count).Select(index => (JournalEntry)(random.Next(3) switch
                {
                    0 => Deposit(cushion, random.Next(1, 100), -random.Next(30)),
                    1 => Deposit(holiday, random.Next(1, 100), -random.Next(30)),
                    _ => Transfer.Create(NewId(), Owner, cushion.BucketId, holiday.BucketId, Money.From(5m, Currency.Pln), Money.From(5m, Currency.Pln), null,
                        Today.AddDays(-random.Next(30)), Today, EntryInfo.None, NextRecordedAt()).Value,
                })).ToList();
                var target = journal[pick % journal.Count];

                var withReversal = Replay([.. journal, Reverse(target)]);
                var without = Replay([.. journal.Where(entry => entry != target)]);

                return withReversal.Balances.SequenceEqual(without.Balances);
            });

    private BudgetState Replay(params JournalEntry[] journal) => BudgetState.Replay([cushion, holiday], journal, Today);

    private Reversal Reverse(JournalEntry entry) =>
        Reversal.Create(NewId(), Owner, entry.EntryId, Today, Today, EntryInfo.None, NextRecordedAt()).Value;

    private Deposit Deposit(Bucket bucket, decimal amount, int day) =>
        Domain.Journal.Deposit.Create(NewId(), Owner, bucket.BucketId, Money.From(amount, bucket.Currency), Today.AddDays(day), Today, EntryInfo.None, NextRecordedAt()).Value;

    private Spend Spend(Bucket bucket, decimal amount, int day) =>
        Domain.Journal.Spend.Create(NewId(), Owner, bucket.BucketId, Money.From(amount, bucket.Currency), Today.AddDays(day), Today, EntryInfo.None, NextRecordedAt()).Value;

    private DateTimeOffset NextRecordedAt() => FakeClock.DefaultStart.AddSeconds(Interlocked.Increment(ref recorded));

    private static JournalEntryId NewId() => JournalEntryId.From(Guid.CreateVersion7());
}
