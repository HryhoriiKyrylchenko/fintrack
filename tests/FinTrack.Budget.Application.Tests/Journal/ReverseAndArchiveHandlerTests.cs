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

    private readonly CallLog log = new();
    private readonly FakeLedger ledger;
    private readonly FakeBudgetStore store;
    private readonly FakeClock clock = new();
    private readonly Bucket cushion;
    private readonly Deposit deposit;

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

    private Task<FluentResults.Result> Reverse(Guid target) => Send(new ReverseEntry(Guid.CreateVersion7(), target, Today, null, []));

    private Task<FluentResults.Result> Send(ReverseEntry command) =>
        new ReverseEntryHandler(new JournalWriter(new BudgetStateLoader(ledger, store), ledger, new FakeOwnerContext(), clock)).Handle(command, Cancellation).AsTask();

    private Task<FluentResults.Result> Archive(Guid bucket) =>
        new ArchiveBucketHandler(new BudgetStateLoader(ledger, store), ledger).Handle(new ArchiveBucket(bucket), Cancellation).AsTask();
}
