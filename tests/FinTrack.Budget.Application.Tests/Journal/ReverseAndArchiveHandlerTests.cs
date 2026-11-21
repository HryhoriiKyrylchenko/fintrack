using FinTrack.Budget.Application.Buckets;
using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Application.Tests.Journal;

/// <summary>
/// <c>ReverseEntry</c> (D3) and <c>ArchiveBucket</c> against in-memory fakes: which entries may be reversed,
/// that a reversal never leaves a bucket below zero, and which buckets may be archived.
/// </summary>
public class ReverseAndArchiveHandlerTests
{
    private static readonly DateOnly Today = new(2026, 1, 15);
    private static readonly EntryInfo Reason = new("Counted the cash box", [], EntrySource.Manual);

    private readonly CallLog log = new();
    private readonly FakeLedger ledger;
    private readonly FakeBudgetStore store;
    private readonly FakeClock clock = new();
    private readonly Bucket cushion;
    private readonly Deposit deposit;
    private IEntryDependencies dependencies = new JournalEntryDependencies();

    public ReverseAndArchiveHandlerTests()
    {
        ledger = new FakeLedger(log);
        store = new FakeBudgetStore(log) { Settings = StoredSettings.Of(Currency.Pln, "Europe/Warsaw", "pl-PL") };
        cushion = StoredBuckets.Of(Guid.CreateVersion7(), "Cushion", Currency.Pln, 1, FakeClock.DefaultStart, BucketStatus.Active);
        store.Buckets.Add(cushion);
        deposit = Deposit.Create(JournalEntryId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, cushion.BucketId, Money.From(100m, Currency.Pln),
            Today.AddDays(-10), Today, EntryInfo.None, FakeClock.DefaultStart).Value;
        store.Journal.Add(deposit);
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reversal_IsAppended_AndTheBalanceIsAsBefore()
    {
        var result = await Reverse(deposit.Id);

        result.IsSuccess.ShouldBeTrue();
        var reversal = ledger.Commits.ShouldHaveSingleItem().Appends.ShouldHaveSingleItem().ShouldBeOfType<Reversal>();
        reversal.ReversedEntryId.ShouldBe(deposit.EntryId);
    }

    [Fact]
    public async Task UnknownEntry_IsNotFound() =>
        (await Reverse(Guid.CreateVersion7())).ShouldFailWith(BudgetErrorCodes.EntryNotFound, ErrorKind.NotFound);

    [Fact]
    public async Task SecondReversal_IsAlreadyReversed_AndAReversalIsNotReversible()
    {
        await Reverse(deposit.Id);
        var reversal = (Reversal)ledger.Commits[0].Appends[0];
        store.Journal.Add(reversal);

        (await Reverse(deposit.Id)).ShouldFailWith(BudgetErrorCodes.EntryAlreadyReversed, ErrorKind.BusinessRule);
        (await Reverse(reversal.Id)).ShouldFailWith(BudgetErrorCodes.EntryNotReversible, ErrorKind.BusinessRule);
        ledger.Commits.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RepeatedReversal_WritesNothing_InsteadOfAlreadyReversed()
    {
        var command = new ReverseEntry(Guid.CreateVersion7(), deposit.Id, Today, null, []);
        await Send(command);
        store.Journal.Add(ledger.Commits[0].Appends[0].ShouldBeOfType<Reversal>());

        (await Send(command)).IsSuccess.ShouldBeTrue();
        ledger.Commits.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ReversingADepositThatWasSpent_IsInsufficientFunds()
    {
        store.Journal.Add(Spend.Create(JournalEntryId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, cushion.BucketId, Money.From(60m, Currency.Pln),
            Today.AddDays(-5), Today, EntryInfo.None, FakeClock.DefaultStart).Value);

        (await Reverse(deposit.Id)).ShouldFailWith(BudgetErrorCodes.BucketInsufficientFunds, ErrorKind.BusinessRule)
            .Parameters["date"].ShouldBe("2026-01-10");
        ledger.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReversingAnEntryOfAnArchivedBucket_IsRejected()
    {
        var archived = StoredBuckets.Of(Guid.CreateVersion7(), "Old", Currency.Pln, 2, FakeClock.DefaultStart, BucketStatus.Archived);
        store.Buckets.Add(archived);
        var spend = Spend.Create(JournalEntryId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, archived.BucketId, Money.From(1m, Currency.Pln),
            Today, Today, EntryInfo.None, FakeClock.DefaultStart).Value;
        store.Journal.Add(spend);

        (await Reverse(spend.Id)).ShouldFailWith(BudgetErrorCodes.BucketArchived, ErrorKind.BusinessRule);
    }

    [Theory]
    [InlineData(AdjustmentDirection.Increase)]
    [InlineData(AdjustmentDirection.Decrease)]
    public async Task ReversingAnAdjustmentOfAnArchivedBucket_IsRejected(AdjustmentDirection direction)
    {
        var archived = StoredBuckets.Of(Guid.CreateVersion7(), "Old", Currency.Pln, 2, FakeClock.DefaultStart, BucketStatus.Archived);
        store.Buckets.Add(archived);
        var adjustment = Adjustment.Create(JournalEntryId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, archived.BucketId, Money.From(1m, Currency.Pln),
            direction, Today, Today, Reason, FakeClock.DefaultStart).Value;
        store.Journal.Add(adjustment);

        (await Reverse(adjustment.Id)).ShouldFailWith(BudgetErrorCodes.BucketArchived, ErrorKind.BusinessRule);
        ledger.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReversingAnAdjustment_RestoresTheBalance_UnlessItFundedALaterSpend()
    {
        // The deposit of 100 on day -10, then a decrease of 30 on day -5, an increase of 50 on day -4 and a spend of 120 on day -3.
        var decrease = Adjustment.Create(JournalEntryId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, cushion.BucketId, Money.From(30m, Currency.Pln),
            AdjustmentDirection.Decrease, Today.AddDays(-5), Today, Reason, FakeClock.DefaultStart).Value;
        var increase = Adjustment.Create(JournalEntryId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, cushion.BucketId, Money.From(50m, Currency.Pln),
            AdjustmentDirection.Increase, Today.AddDays(-4), Today, Reason, FakeClock.DefaultStart).Value;
        var spend = Spend.Create(JournalEntryId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, cushion.BucketId, Money.From(120m, Currency.Pln),
            Today.AddDays(-3), Today, EntryInfo.None, FakeClock.DefaultStart).Value;
        store.Journal.AddRange([decrease, increase, spend]);

        (await Reverse(increase.Id)).ShouldFailWith(BudgetErrorCodes.BucketInsufficientFunds, ErrorKind.BusinessRule)
            .Parameters["date"].ShouldBe("2026-01-12");
        (await Reverse(decrease.Id)).IsSuccess.ShouldBeTrue();
        ledger.Commits.ShouldHaveSingleItem().Appends.ShouldHaveSingleItem().ShouldBeOfType<Reversal>().ReversedEntryId.ShouldBe(decrease.EntryId);
    }

    [Fact]
    public async Task EmptyBucket_IsArchived()
    {
        var empty = StoredBuckets.Of(Guid.CreateVersion7(), "Holiday", Currency.Pln, 3, FakeClock.DefaultStart, BucketStatus.Active);
        store.Buckets.Add(empty);

        (await Archive(empty.Id)).IsSuccess.ShouldBeTrue();

        var saved = ledger.Commits.ShouldHaveSingleItem().Saves.ShouldHaveSingleItem().ShouldBeOfType<Bucket>();
        saved.Status.ShouldBe(BucketStatus.Archived);
        saved.Version.ShouldBe(3);
    }

    [Fact]
    public async Task BucketWithMoney_IsNotEmpty() =>
        (await Archive(cushion.Id)).ShouldFailWith(BudgetErrorCodes.BucketNotEmpty, ErrorKind.BusinessRule);

    [Fact]
    public async Task BucketEmptiedByAReversal_CanBeArchived()
    {
        await Reverse(deposit.Id);
        store.Journal.Add(ledger.Commits[0].Appends[0].ShouldBeOfType<Reversal>());

        (await Archive(cushion.Id)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ArchivedBucket_ArchivedAgain_WritesNothing()
    {
        var archived = StoredBuckets.Of(Guid.CreateVersion7(), "Old", Currency.Pln, 2, FakeClock.DefaultStart, BucketStatus.Archived);
        store.Buckets.Add(archived);

        (await Archive(archived.Id)).IsSuccess.ShouldBeTrue();
        ledger.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task SystemBucket_AndUnknownBucket_AreRejected()
    {
        var unallocated = Bucket.Unallocated(BucketId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, Currency.Eur);
        store.Buckets.Add(unallocated);

        (await Archive(unallocated.Id)).ShouldFailWith(BudgetErrorCodes.BucketSystemBucket, ErrorKind.BusinessRule);
        (await Archive(Guid.CreateVersion7())).ShouldFailWith(BudgetErrorCodes.BucketNotFound, ErrorKind.NotFound);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReversingATransfer_ThatTouchesAnArchivedBucket_OnEitherSide_IsRejected(bool archivedIsTheDestination)
    {
        // The other side is active: one archived bucket is enough, because reversing would change its balance.
        var old = StoredBuckets.Of(Guid.CreateVersion7(), "Old", Currency.Pln, 2, FakeClock.DefaultStart, BucketStatus.Archived);
        store.Buckets.Add(old);
        var (from, to) = archivedIsTheDestination ? (cushion.BucketId, old.BucketId) : (old.BucketId, cushion.BucketId);
        var transfer = Transfer.Create(JournalEntryId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, from, to, Money.From(10m, Currency.Pln),
            Money.From(10m, Currency.Pln), null, Today.AddDays(-5), Today, EntryInfo.None, FakeClock.DefaultStart.AddSeconds(1)).Value;
        store.Journal.Add(transfer);

        (await Reverse(transfer.Id)).ShouldFailWith(BudgetErrorCodes.BucketArchived, ErrorKind.BusinessRule);
        ledger.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task EntryThatOthersReferTo_IsHasDependents_AndNothingIsWritten()
    {
        var lookup = new FakeEntryDependencies(deposit.Id);
        dependencies = lookup;

        (await Reverse(deposit.Id)).ShouldFailWith(BudgetErrorCodes.EntryHasDependents, ErrorKind.BusinessRule);

        ledger.Commits.ShouldBeEmpty();
        lookup.Asked.ShouldBe([deposit.Id]);
    }

    [Fact]
    public async Task EntryWithoutDependents_IsReversed_AfterTheLookup()
    {
        var lookup = new FakeEntryDependencies(Guid.CreateVersion7());
        dependencies = lookup;

        (await Reverse(deposit.Id)).IsSuccess.ShouldBeTrue();

        ledger.Commits.ShouldHaveSingleItem();
        lookup.Asked.ShouldBe([deposit.Id]);
    }

    [Fact]
    public async Task AlreadyReversed_IsReportedBeforeHasDependents()
    {
        await Reverse(deposit.Id);
        store.Journal.Add(ledger.Commits[0].Appends[0].ShouldBeOfType<Reversal>());
        dependencies = new FakeEntryDependencies(deposit.Id);

        (await Reverse(deposit.Id)).ShouldFailWith(BudgetErrorCodes.EntryAlreadyReversed, ErrorKind.BusinessRule);
    }

    [Fact]
    public async Task HasDependents_IsReportedBeforeArchived()
    {
        var archived = StoredBuckets.Of(Guid.CreateVersion7(), "Old", Currency.Pln, 2, FakeClock.DefaultStart, BucketStatus.Archived);
        store.Buckets.Add(archived);
        var spend = Spend.Create(JournalEntryId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, archived.BucketId, Money.From(1m, Currency.Pln),
            Today, Today, EntryInfo.None, FakeClock.DefaultStart).Value;
        store.Journal.Add(spend);
        dependencies = new FakeEntryDependencies(spend.Id);

        (await Reverse(spend.Id)).ShouldFailWith(BudgetErrorCodes.EntryHasDependents, ErrorKind.BusinessRule);
    }

    // No entry type of phase 1a refers to another entry except a reversal, which is never effective (B5: loans, plans
    // and pauses add their cases in steps 5, 9 and 10).
    [Theory]
    [InlineData("OpeningBalance")]
    [InlineData("Deposit")]
    [InlineData("Spend")]
    [InlineData("Transfer")]
    [InlineData("Reversal")]
    [InlineData("Adjustment")]
    public void ThePhase1aLookup_FindsNoDependents(string type)
    {
        var id = JournalEntryId.From(Guid.CreateVersion7());
        var amount = Money.From(1m, Currency.Pln);
        JournalEntry entry = type switch
        {
            "OpeningBalance" => OpeningBalance.Create(id, FakeOwnerContext.Owner, cushion.BucketId, amount, Today, Today, EntryInfo.None, FakeClock.DefaultStart).Value,
            "Deposit" => deposit,
            "Spend" => Spend.Create(id, FakeOwnerContext.Owner, cushion.BucketId, amount, Today, Today, EntryInfo.None, FakeClock.DefaultStart).Value,
            "Transfer" => Transfer.Create(id, FakeOwnerContext.Owner, cushion.BucketId, BucketId.From(Guid.CreateVersion7()), amount, amount, null, Today, Today, EntryInfo.None, FakeClock.DefaultStart).Value,
            "Adjustment" => Adjustment.Create(id, FakeOwnerContext.Owner, cushion.BucketId, amount, AdjustmentDirection.Increase, Today, Today, Reason, FakeClock.DefaultStart).Value,
            _ => Reversal.Create(id, FakeOwnerContext.Owner, deposit.EntryId, Today, Today, EntryInfo.None, FakeClock.DefaultStart).Value,
        };
        var snapshot = new BudgetSnapshot(1, store.Settings, [.. store.Buckets], [.. store.Journal, entry]);

        new JournalEntryDependencies().HasDependents(entry, snapshot).ShouldBeFalse();
        new JournalEntryDependencies().HasDependents(deposit, snapshot).ShouldBeFalse();
    }

    [Fact]
    public async Task ArchiveBeforeTheFirstRun_IsSettingsNotFound()
    {
        store.Settings = null;
        var empty = StoredBuckets.Of(Guid.CreateVersion7(), "Holiday", Currency.Pln, 3, FakeClock.DefaultStart, BucketStatus.Active);
        store.Buckets.Add(empty);

        (await Archive(empty.Id)).ShouldFailWith(BudgetErrorCodes.SettingsNotFound, ErrorKind.NotFound);
        ledger.Commits.ShouldBeEmpty();
    }

    private Task<FluentResults.Result> Reverse(Guid target) => Send(new ReverseEntry(Guid.CreateVersion7(), target, Today, null, []));

    private Task<FluentResults.Result> Send(ReverseEntry command) =>
        new ReverseEntryHandler(new JournalWriter(new BudgetStateLoader(ledger, store), ledger, new FakeOwnerContext(), clock), dependencies).Handle(command, Cancellation).AsTask();

    private Task<FluentResults.Result> Archive(Guid bucket) =>
        new ArchiveBucketHandler(new BudgetStateLoader(ledger, store), ledger, clock).Handle(new ArchiveBucket(bucket), Cancellation).AsTask();

    /// <summary>A dependency lookup that reports the given entries as referred to, and records what it was asked.</summary>
    private sealed class FakeEntryDependencies(params Guid[] withDependents) : IEntryDependencies
    {
        public List<Guid> Asked { get; } = [];

        public bool HasDependents(JournalEntry entry, BudgetSnapshot state)
        {
            Asked.Add(entry.Id);
            return withDependents.Contains(entry.Id);
        }
    }
}
