using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Contracts.V1.Journal;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Application.Tests.Journal;

/// <summary>
/// <c>GetJournal</c> against in-memory fakes: newest first in the reverse replay order, every type's
/// fields, reversed entries linked to their reversal, the filters, and paging with the cursor.
/// </summary>
public class GetJournalHandlerTests
{
    private static readonly DateOnly Today = new(2026, 1, 15);

    private readonly CallLog log = new();
    private readonly FakeLedger ledger;
    private readonly FakeBudgetStore store;
    private readonly Bucket cushion;
    private readonly Bucket holiday;
    private readonly Bucket euro;
    private int recorded;

    public GetJournalHandlerTests()
    {
        ledger = new FakeLedger(log);
        store = new FakeBudgetStore(log) { Settings = StoredSettings.Of(Currency.Pln, "Europe/Warsaw", "pl-PL") };
        cushion = Bucket("Cushion", Currency.Pln, 1);
        holiday = Bucket("Holiday", Currency.Pln, 2);
        euro = Bucket("Euro", Currency.Eur, 3);
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EmptyJournal_IsOneEmptyPage()
    {
        var page = (await Journal(new GetJournal())).Value;

        page.Entries.ShouldBeEmpty();
        page.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task Entries_AreNewestFirst_InTheReverseReplayOrder()
    {
        // Added out of order: by date, then by recorded time on the same date, then by ID within one commit.
        var late = Deposit(cushion, 1m, Today.AddDays(-1));
        var early = Deposit(cushion, 2m, Today.AddDays(-9));
        var sameDayFirst = Deposit(cushion, 3m, Today.AddDays(-5));
        var sameDaySecond = Deposit(cushion, 4m, Today.AddDays(-5));
        var commit = NextRecordedAt();
        var (low, high) = OrderedIds();
        var oneCommitHigh = Deposit(cushion, 5m, Today.AddDays(-3), commit, high);
        var oneCommitLow = Deposit(cushion, 6m, Today.AddDays(-3), commit, low);

        var page = (await Journal(new GetJournal())).Value;

        page.Entries.Select(entry => entry.Id).ShouldBe([late.Id, oneCommitHigh.Id, oneCommitLow.Id, sameDaySecond.Id, sameDayFirst.Id, early.Id]);
    }

    [Fact]
    public async Task SingleBucketEntries_CarryTheirFields()
    {
        var opening = Add(OpeningBalance.Create(NewId(), FakeOwnerContext.Owner, cushion.BucketId, Pln(500m), Today.AddDays(-30), Today, EntryInfo.None, NextRecordedAt()).Value);
        var deposit = Add(Domain.Journal.Deposit.Create(NewId(), FakeOwnerContext.Owner, cushion.BucketId, Pln(12.30m), Today.AddDays(-2), Today,
            new EntryInfo("Salary", ["work", "monthly"], EntrySource.Manual), NextRecordedAt()).Value);
        var spend = Add(Spend.Create(NewId(), FakeOwnerContext.Owner, cushion.BucketId, Pln(0.30m), Today, Today, EntryInfo.None, NextRecordedAt()).Value);

        var entries = (await Journal(new GetJournal())).Value.Entries;

        entries.Select(Untagged).ShouldBe(
        [
            Single(spend, JournalEntryType.Spend, null, Pln(0.30m)),
            Single(deposit, JournalEntryType.Deposit, "Salary", Pln(12.30m)),
            Single(opening, JournalEntryType.OpeningBalance, null, Pln(500m)),
        ]);
        entries.Select(entry => string.Join('|', entry.Tags)).ShouldBe(["", "work|monthly", ""]);
    }

    [Fact]
    public async Task Transfer_CarriesBothSides_TheFeeAndTheRate()
    {
        Deposit(cushion, 100m, Today.AddDays(-9));
        var transfer = Add(Domain.Journal.Transfer.Create(NewId(), FakeOwnerContext.Owner, cushion.BucketId, euro.BucketId, Pln(43.50m), Eur(10.00m), Pln(0.50m),
            Today.AddDays(-1), Today, EntryInfo.None, NextRecordedAt()).Value);

        var entry = (await Journal(new GetJournal())).Value.Entries[0];

        entry.Tags.ShouldBeEmpty();
        Untagged(entry).ShouldBe(new JournalEntryDto(
            transfer.Id, JournalEntryType.Transfer, Today.AddDays(-1), transfer.RecordedAt, null, NoTags, null, "Manual", null,
            null, null, cushion.Id, euro.Id, Pln(43.50m), Eur(10.00m), Pln(0.50m), transfer.Rate, null, null));
        entry.Rate.ShouldBe(0.232558139535m);
    }

    [Fact]
    public async Task ReversedEntry_NamesItsReversal_AndTheReversalNamesTheEntry()
    {
        var kept = Deposit(cushion, 10m, Today.AddDays(-4));
        var mistake = Deposit(cushion, 1000m, Today.AddDays(-3));
        var reversal = Reverse(mistake, "Typo");

        var entries = (await Journal(new GetJournal())).Value.Entries;

        entries.Select(entry => (entry.Id, entry.ReversedBy, entry.ReversedEntryId)).ShouldBe(
        [
            (reversal.Id, null, mistake.Id),
            (mistake.Id, reversal.Id, null),
            (kept.Id, null, null),
        ]);
        Untagged(entries[0]).ShouldBe(new JournalEntryDto(
            reversal.Id, JournalEntryType.Reversal, Today, reversal.RecordedAt, "Typo", NoTags, null, "Manual", null,
            null, null, null, null, null, null, null, null, mistake.Id, null));
    }

    [Fact]
    public async Task BucketFilter_KeepsItsEntries_BothSidesOfTransfers_AndReversalsOfThem()
    {
        var intoCushion = Deposit(cushion, 100m, Today.AddDays(-9));
        var intoHoliday = Deposit(holiday, 50m, Today.AddDays(-8));
        var move = Add(Domain.Journal.Transfer.Create(NewId(), FakeOwnerContext.Owner, cushion.BucketId, holiday.BucketId, Pln(10m), Pln(10m), null,
            Today.AddDays(-7), Today, EntryInfo.None, NextRecordedAt()).Value);
        var reverseMove = Reverse(move, null);
        var reverseHoliday = Reverse(intoHoliday, null);

        (await Ids(new GetJournal(BucketId: cushion.Id))).ShouldBe([reverseMove.Id, move.Id, intoCushion.Id]);
        (await Ids(new GetJournal(BucketId: holiday.Id))).ShouldBe([reverseHoliday.Id, reverseMove.Id, move.Id, intoHoliday.Id]);
        (await Ids(new GetJournal(BucketId: euro.Id))).ShouldBeEmpty();
    }

    [Fact]
    public async Task BucketFilter_OfAnUnknownBucket_IsNotFound() =>
        (await Journal(new GetJournal(BucketId: Guid.CreateVersion7()))).ShouldFailWith(BudgetErrorCodes.BucketNotFound, ErrorKind.NotFound);

    [Fact]
    public async Task BucketFilter_MayNameAnArchivedBucket()
    {
        var old = Bucket("Old", Currency.Pln, 4, BucketStatus.Archived);

        (await Journal(new GetJournal(BucketId: old.Id))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task DateFilters_AreInclusive()
    {
        Deposit(cushion, 1m, Today.AddDays(-10));
        var first = Deposit(cushion, 2m, Today.AddDays(-9));
        var middle = Deposit(cushion, 3m, Today.AddDays(-8));
        var last = Deposit(cushion, 4m, Today.AddDays(-7));
        Deposit(cushion, 5m, Today.AddDays(-6));

        (await Ids(new GetJournal(From: Today.AddDays(-9), To: Today.AddDays(-7)))).ShouldBe([last.Id, middle.Id, first.Id]);
        (await Ids(new GetJournal(From: Today.AddDays(-8), To: Today.AddDays(-8)))).ShouldBe([middle.Id]);
        (await Ids(new GetJournal(From: Today.AddDays(-7)))).Count.ShouldBe(2);
        (await Ids(new GetJournal(To: Today.AddDays(-9)))).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Paging_ReturnsEveryEntryOnce_InOrder_AndEndsWithoutACursor()
    {
        var all = Enumerable.Range(1, 5).Select(day => Deposit(cushion, day, Today.AddDays(-day))).ToList();

        var first = (await Journal(new GetJournal(Limit: 2))).Value;
        var second = (await Journal(new GetJournal(Limit: 2, Cursor: first.NextCursor))).Value;
        var third = (await Journal(new GetJournal(Limit: 2, Cursor: second.NextCursor))).Value;

        first.Entries.Select(entry => entry.Id).ShouldBe([all[0].Id, all[1].Id]);
        second.Entries.Select(entry => entry.Id).ShouldBe([all[2].Id, all[3].Id]);
        third.Entries.Select(entry => entry.Id).ShouldBe([all[4].Id]);
        first.NextCursor.ShouldNotBeNull();
        second.NextCursor.ShouldNotBeNull();
        third.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task APageThatTakesTheLastEntries_HasNoCursor()
    {
        Deposit(cushion, 1m, Today.AddDays(-1));
        Deposit(cushion, 2m, Today.AddDays(-2));

        (await Journal(new GetJournal(Limit: 2))).Value.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task Paging_WithAFilter_UsesTheSameCursor()
    {
        var wanted = Enumerable.Range(1, 3).Select(day => Deposit(cushion, day, Today.AddDays(-2 * day))).ToList();
        Enumerable.Range(1, 3).ToList().ForEach(day => Deposit(holiday, day, Today.AddDays(-(2 * day) - 1)));

        var first = (await Journal(new GetJournal(BucketId: cushion.Id, Limit: 2))).Value;
        var second = (await Journal(new GetJournal(BucketId: cushion.Id, Limit: 2, Cursor: first.NextCursor))).Value;

        first.Entries.Concat(second.Entries).Select(entry => entry.Id).ShouldBe(wanted.Select(entry => entry.Id));
        second.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task EntriesAddedWhilePaging_AppearWhereTheirPositionPutsThem()
    {
        var newest = Deposit(cushion, 1m, Today.AddDays(-1));
        var middle = Deposit(cushion, 2m, Today.AddDays(-5));
        var oldest = Deposit(cushion, 3m, Today.AddDays(-9));
        var first = (await Journal(new GetJournal(Limit: 1))).Value;

        // One entry newer than the page already read, one backdated between the remaining ones.
        Deposit(cushion, 4m, Today);
        var backdated = Deposit(cushion, 5m, Today.AddDays(-7));

        var rest = (await Journal(new GetJournal(Limit: 10, Cursor: first.NextCursor))).Value;

        first.Entries.ShouldHaveSingleItem().Id.ShouldBe(newest.Id);
        rest.Entries.Select(entry => entry.Id).ShouldBe([middle.Id, backdated.Id, oldest.Id]);
    }

    [Fact]
    public async Task ReadsTheStateThroughTheLoader_AndWritesNothing()
    {
        Deposit(cushion, 1m, Today);

        await Journal(new GetJournal());

        log.Calls.ShouldBe(["version", "settings", "buckets", "journal", "version"]);
        ledger.Commits.ShouldBeEmpty();
    }

    private Task<FluentResults.Result<JournalPageDto>> Journal(GetJournal query) =>
        new GetJournalHandler(new BudgetStateLoader(ledger, store)).Handle(query, Cancellation).AsTask();

    private async Task<IReadOnlyList<Guid>> Ids(GetJournal query) =>
        [.. (await Journal(query)).Value.Entries.Select(entry => entry.Id)];

    private Bucket Bucket(string name, Currency currency, int minute, BucketStatus status = BucketStatus.Active)
    {
        var bucket = StoredBuckets.Of(Guid.CreateVersion7(), name, currency, 1, FakeClock.DefaultStart.AddDays(-60).AddMinutes(minute), status);
        store.Buckets.Add(bucket);
        return bucket;
    }

    private Deposit Deposit(Bucket bucket, decimal amount, DateOnly occurredOn, DateTimeOffset? recordedAt = null, Guid? id = null) =>
        Add(Domain.Journal.Deposit.Create(JournalEntryId.From(id ?? Guid.CreateVersion7()), FakeOwnerContext.Owner, bucket.BucketId,
            Money.From(amount, bucket.Currency), occurredOn, Today, EntryInfo.None, recordedAt ?? NextRecordedAt()).Value);

    private Reversal Reverse(JournalEntry entry, string? note) =>
        Add(Reversal.Create(NewId(), FakeOwnerContext.Owner, entry.EntryId, Today, Today, new EntryInfo(note, [], EntrySource.Manual), NextRecordedAt()).Value);

    private T Add<T>(T entry)
        where T : JournalEntry
    {
        store.Journal.Add(entry);
        return entry;
    }

    // A record compares its tags list by reference; tests compare the tags on their own and the rest with one shared list.
    private static readonly IReadOnlyList<string> NoTags = [];

    private static JournalEntryDto Untagged(JournalEntryDto entry) => entry with { Tags = NoTags };

    private static JournalEntryDto Single(SingleBucketEntry entry, JournalEntryType type, string? note, Money amount) =>
        new(entry.Id, type, entry.OccurredOn, entry.RecordedAt, note, NoTags, null, "Manual", null,
            entry.BucketId.Value, amount, null, null, null, null, null, null, null, null);

    // Two IDs whose big-endian order is known (the replay order's tie-break within one commit).
    private static (Guid Low, Guid High) OrderedIds()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddMinutes(1));
        return ReplayOrder.CompareIds(first, second) < 0 ? (first, second) : (second, first);
    }

    private static JournalEntryId NewId() => JournalEntryId.From(Guid.CreateVersion7());

    // Distinct, increasing times, so the order never depends on random ID bits.
    private DateTimeOffset NextRecordedAt() => FakeClock.DefaultStart.AddDays(-10).AddSeconds(++recorded);

    private static Money Pln(decimal amount) => Money.From(amount, Currency.Pln);

    private static Money Eur(decimal amount) => Money.From(amount, Currency.Eur);
}
