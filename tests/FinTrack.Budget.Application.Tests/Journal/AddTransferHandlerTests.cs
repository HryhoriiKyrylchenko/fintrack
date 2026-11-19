using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Application.Tests.Journal;

/// <summary>
/// <c>AddTransfer</c> against in-memory fakes: both sides resolved (with <c>Unallocated</c> on demand),
/// both buckets checked, the amounts' rules (D6), never below zero (D2), repeats (D4).
/// </summary>
public class AddTransferHandlerTests
{
    private static readonly DateOnly Today = new(2026, 1, 15);

    private readonly CallLog log = new();
    private readonly FakeLedger ledger;
    private readonly FakeBudgetStore store;
    private readonly FakeClock clock = new();
    private readonly Bucket cushion;
    private readonly Bucket holiday;

    public AddTransferHandlerTests()
    {
        ledger = new FakeLedger(log);
        store = new FakeBudgetStore(log) { Settings = StoredSettings.Of(Currency.Pln, "Europe/Warsaw", "pl-PL") };
        cushion = StoredBuckets.Of(Guid.CreateVersion7(), "Cushion", Currency.Pln, 1, FakeClock.DefaultStart, BucketStatus.Active);
        holiday = StoredBuckets.Of(Guid.CreateVersion7(), "Holiday", Currency.Pln, 1, FakeClock.DefaultStart, BucketStatus.Active);
        store.Buckets.AddRange([cushion, holiday]);
        store.Journal.Add(Deposit.Create(JournalEntryId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, cushion.BucketId, Pln(100m),
            Today.AddDays(-10), Today, EntryInfo.None, FakeClock.DefaultStart).Value);
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TransferBetweenBuckets_AppendsOneEntry()
    {
        var result = await Send(Between(cushion, holiday, Pln(60m), Pln(59.50m), Pln(0.50m)));

        result.IsSuccess.ShouldBeTrue();
        var transfer = ledger.Commits.ShouldHaveSingleItem().Appends.ShouldHaveSingleItem().ShouldBeOfType<Transfer>();
        transfer.FromBucketId.ShouldBe(cushion.BucketId);
        transfer.ToBucketId.ShouldBe(holiday.BucketId);
        transfer.Fee.ShouldBe(Pln(0.50m));
    }

    [Fact]
    public async Task TransferIntoANewUnallocated_CreatesItInTheSameCommit()
    {
        var result = await Send(new AddTransfer(Guid.CreateVersion7(), Pln(40m), Money.From(9.30m, Currency.Eur), null, Today, cushion.Id, null, null, Currency.Eur, null, []));

        result.IsSuccess.ShouldBeTrue();
        var commit = ledger.Commits.ShouldHaveSingleItem();
        var unallocated = commit.Saves.ShouldHaveSingleItem().ShouldBeOfType<Bucket>();
        unallocated.Currency.ShouldBe(Currency.Eur);
        commit.Appends.ShouldHaveSingleItem().ShouldBeOfType<Transfer>().ToBucketId.ShouldBe(unallocated.BucketId);
    }

    [Fact]
    public async Task UnallocatedToTheSameUnallocated_IsTheSameBucket()
    {
        (await Send(new AddTransfer(Guid.CreateVersion7(), Pln(1m), Pln(1m), null, Today, null, Currency.Pln, null, Currency.Pln, null, [])))
            .ShouldFailWith(BudgetErrorCodes.TransferSameBucket, ErrorKind.Validation);
        ledger.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task TransferBeyondTheSourcesBalance_IsInsufficientFunds()
    {
        (await Send(Between(cushion, holiday, Pln(100.01m), Pln(100.01m))))
            .ShouldFailWith(BudgetErrorCodes.BucketInsufficientFunds, ErrorKind.BusinessRule);
        ledger.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task EachAmountMustBeInItsBucketsCurrency()
    {
        var eur = StoredBuckets.Of(Guid.CreateVersion7(), "Euro", Currency.Eur, 1, FakeClock.DefaultStart, BucketStatus.Active);
        store.Buckets.Add(eur);

        (await Send(Between(cushion, eur, Pln(10m), Money.From(10m, Currency.Usd))))
            .ShouldFailWith(BudgetErrorCodes.BucketCurrencyMismatch, ErrorKind.BusinessRule);
        (await Send(Between(eur, cushion, Pln(10m), Pln(10m))))
            .ShouldFailWith(BudgetErrorCodes.BucketCurrencyMismatch, ErrorKind.BusinessRule);
    }

    [Fact]
    public async Task ArchivedDestination_IsRejected()
    {
        var archived = StoredBuckets.Of(Guid.CreateVersion7(), "Old", Currency.Pln, 1, FakeClock.DefaultStart, BucketStatus.Archived);
        store.Buckets.Add(archived);

        (await Send(Between(cushion, archived, Pln(1m), Pln(1m)))).ShouldFailWith(BudgetErrorCodes.BucketArchived, ErrorKind.BusinessRule);
    }

    [Fact]
    public async Task UnknownDestination_IsNotFound()
    {
        (await Send(new AddTransfer(Guid.CreateVersion7(), Pln(1m), Pln(1m), null, Today, cushion.Id, null, Guid.CreateVersion7(), null, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketNotFound, ErrorKind.NotFound);
        ledger.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task UnknownSource_IsNotFound() =>
        (await Send(new AddTransfer(Guid.CreateVersion7(), Pln(1m), Pln(1m), null, Today, Guid.CreateVersion7(), null, holiday.Id, null, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketNotFound, ErrorKind.NotFound);

    [Fact]
    public async Task InconsistentSameCurrencyAmounts_AreRejected() =>
        (await Send(Between(cushion, holiday, Pln(10m), Pln(9m)))).ShouldFailWith(BudgetErrorCodes.TransferAmountsInconsistent, ErrorKind.Validation);

    [Fact]
    public async Task RepeatedTransfer_WritesNothing_AlsoWhenItWouldNowOverdraw()
    {
        var command = Between(cushion, holiday, Pln(100m), Pln(100m));
        await Send(command);
        store.Journal.AddRange(ledger.Commits[0].Appends.Cast<JournalEntry>());

        (await Send(command)).IsSuccess.ShouldBeTrue();
        (await Send(command with { Note = "Other" })).ShouldFailWith(BudgetErrorCodes.EntryIdReused, ErrorKind.Conflict);
        ledger.Commits.Count.ShouldBe(1);
    }

    [Fact]
    public void Validator_NeedsExactlyOneTargetOnEachSide()
    {
        var errors = new AddTransferValidator().Validate(new AddTransfer(Guid.CreateVersion7(), Pln(1m), Pln(1m), null, Today, cushion.Id, Currency.Pln, null, null, null, [])).Errors;

        errors.ShouldContain(error => error.PropertyName == "FromBucketId" && error.ErrorCode == "ExactlyOneTarget");
        errors.ShouldContain(error => error.PropertyName == "ToBucketId" && error.ErrorCode == "ExactlyOneTarget");
    }

    private AddTransfer Between(Bucket from, Bucket to, Money fromAmount, Money toAmount, Money? fee = null) =>
        new(Guid.CreateVersion7(), fromAmount, toAmount, fee, Today, from.Id, null, to.Id, null, null, []);

    private Task<FluentResults.Result> Send(AddTransfer command) =>
        new AddTransferHandler(new JournalWriter(new BudgetStateLoader(ledger, store), ledger, new FakeOwnerContext(), clock)).Handle(command, Cancellation).AsTask();

    private static Money Pln(decimal amount) => Money.From(amount, Currency.Pln);
}
