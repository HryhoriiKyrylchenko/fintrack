using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;
using Contract = FinTrack.Budget.Contracts.V1.Journal;

namespace FinTrack.Budget.Application.Tests.Journal;

/// <summary>
/// <c>AddAdjustment</c> (plan 1b B14) against in-memory fakes: both directions, the required note, any active bucket
/// (investment and <c>Unallocated</c> included), a decrease held to invariant 1 with the first day below zero, and
/// repeats (invariant 13).
/// </summary>
public class AddAdjustmentHandlerTests
{
    // FakeClock.DefaultStart is 2026-01-15 09:30 UTC: in Warsaw that is 2026-01-15.
    private static readonly DateOnly Today = new(2026, 1, 15);

    private readonly CallLog log = new();
    private readonly FakeLedger ledger;
    private readonly FakeBudgetStore store;
    private readonly FakeClock clock = new();
    private readonly Bucket cushion;

    public AddAdjustmentHandlerTests()
    {
        ledger = new FakeLedger(log);
        store = new FakeBudgetStore(log) { Settings = StoredSettings.Of(Currency.Pln, "Europe/Warsaw", "pl-PL") };
        cushion = StoredBuckets.Of(Guid.CreateVersion7(), "Cushion", Currency.Pln, 1, FakeClock.DefaultStart, BucketStatus.Active);
        store.Buckets.Add(cushion);
        store.Journal.Add(Deposit.Create(JournalEntryId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, cushion.BucketId, Money.From(100m, Currency.Pln),
            Today.AddDays(-10), Today, EntryInfo.None, FakeClock.DefaultStart.AddDays(-10)).Value);
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(Contract.AdjustmentDirection.Increase, AdjustmentDirection.Increase)]
    [InlineData(Contract.AdjustmentDirection.Decrease, AdjustmentDirection.Decrease)]
    public async Task Adjustment_AppendsOneEntry_WithItsDirection_UnderTheVersionReadFirst(Contract.AdjustmentDirection sent, AdjustmentDirection stored)
    {
        ledger.Version = 3;
        var id = Guid.CreateVersion7();

        var result = await Send(new AddAdjustment(id, Money.From(12.34m, Currency.Pln), sent, Today.AddDays(-1), cushion.Id, null, "Counted the cash box", ["cash"]));

        result.IsSuccess.ShouldBeTrue();
        log.Calls.ShouldBe(["version", "settings", "buckets", "journal", "version", "commit"]);
        var commit = ledger.Commits.ShouldHaveSingleItem();
        commit.ExpectedVersion.ShouldBe(3);
        commit.Saves.ShouldBeEmpty();
        var adjustment = commit.Appends.ShouldHaveSingleItem().ShouldBeOfType<Adjustment>();
        adjustment.Id.ShouldBe(id);
        adjustment.OwnerId.ShouldBe(FakeOwnerContext.Owner);
        adjustment.BucketId.ShouldBe(cushion.BucketId);
        adjustment.Amount.ShouldBe(Money.From(12.34m, Currency.Pln));
        adjustment.Direction.ShouldBe(stored);
        adjustment.OccurredOn.ShouldBe(Today.AddDays(-1));
        adjustment.RecordedAt.ShouldBe(FakeClock.DefaultStart);
        adjustment.Note.ShouldBe("Counted the cash box");
        adjustment.Tags.ShouldBe(["cash"]);
        adjustment.Source.ShouldBe(EntrySource.Manual);
        adjustment.GroupId.ShouldBeNull();
    }

    [Theory]
    [InlineData(Contract.AdjustmentDirection.Increase, 112.34, 12.34, 0)]
    [InlineData(Contract.AdjustmentDirection.Decrease, 87.66, 0, 12.34)]
    public async Task TheStateAfterwards_ChangesNetContributed_ButNeverIncome(Contract.AdjustmentDirection direction, double net, double adjustedIn, double adjustedOut)
    {
        (await Send(Adjust(direction, 12.34m, -1))).IsSuccess.ShouldBeTrue();

        var balance = BudgetState.Replay(store.Buckets, [.. store.Journal, .. ledger.Commits[0].Appends.Cast<JournalEntry>()], Today).BalanceOf(cushion.BucketId);
        balance.NetContributed.ShouldBe(Money.From((decimal)net, Currency.Pln));
        balance.Deposited.ShouldBe(Money.From(100m, Currency.Pln));
        balance.AdjustedIn.ShouldBe(Money.From((decimal)adjustedIn, Currency.Pln));
        balance.AdjustedOut.ShouldBe(Money.From((decimal)adjustedOut, Currency.Pln));
    }

    [Fact]
    public async Task ADecreaseToExactlyZero_IsAccepted()
    {
        (await Send(Adjust(Contract.AdjustmentDirection.Decrease, 100m, 0))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ADecreaseBelowZero_IsInsufficientFunds_WithTheDay_AndWritesNothing()
    {
        var error = (await Send(Adjust(Contract.AdjustmentDirection.Decrease, 100.01m, -1)))
            .ShouldFailWith(BudgetErrorCodes.BucketInsufficientFunds, ErrorKind.BusinessRule);

        error.Parameters.ShouldBe(new Dictionary<string, object> { ["date"] = "2026-01-14" });
        ledger.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task ADecreaseBeforeTheMoneyArrived_IsInsufficientFunds_OnThatDay_EvenThoughTodayIsFine()
    {
        (await Send(Adjust(Contract.AdjustmentDirection.Decrease, 1m, -20)))
            .ShouldFailWith(BudgetErrorCodes.BucketInsufficientFunds, ErrorKind.BusinessRule)
            .Parameters["date"].ShouldBe("2025-12-26");
        ledger.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnIncrease_IsNotHeldToAnyBalance()
    {
        (await Send(Adjust(Contract.AdjustmentDirection.Increase, 1_000_000m, -30))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task AnInvestmentBucket_MayBeAdjustedBelowZero()
    {
        var crypto = StoredBuckets.Of(Guid.CreateVersion7(), "Crypto", Currency.Usd, FakeClock.DefaultStart, BucketStatus.Active, BucketKind.Investment, InvestmentProfile.Crypto, BucketSystemRole.None);
        store.Buckets.Add(crypto);

        (await Send(new AddAdjustment(Guid.CreateVersion7(), Money.From(5m, Currency.Usd), Contract.AdjustmentDirection.Decrease, Today, crypto.Id, null, "Fees seen on the statement", [])))
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task UnallocatedIsAdjusted_AndCreatedInTheSameCommit_WhenMissing()
    {
        (await Send(new AddAdjustment(Guid.CreateVersion7(), Money.From(3m, Currency.Eur), Contract.AdjustmentDirection.Increase, Today, null, Currency.Eur, "Found in a pocket", [])))
            .IsSuccess.ShouldBeTrue();

        var commit = ledger.Commits.ShouldHaveSingleItem();
        var unallocated = commit.Saves.ShouldHaveSingleItem().ShouldBeOfType<Bucket>();
        unallocated.SystemRole.ShouldBe(BucketSystemRole.Unallocated);
        commit.Appends.ShouldHaveSingleItem().ShouldBeOfType<Adjustment>().BucketId.ShouldBe(unallocated.BucketId);
    }

    [Fact]
    public async Task ADecreaseOfAMissingUnallocated_IsInsufficientFunds_AndCreatesNothing()
    {
        (await Send(new AddAdjustment(Guid.CreateVersion7(), Money.From(1m, Currency.Usd), Contract.AdjustmentDirection.Decrease, Today, null, Currency.Usd, "Recount", [])))
            .ShouldFailWith(BudgetErrorCodes.BucketInsufficientFunds, ErrorKind.BusinessRule);
        ledger.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnArchivedBucket_AnotherCurrency_AnUnknownBucket_AndAFutureDate_AreRejected()
    {
        var archived = StoredBuckets.Of(Guid.CreateVersion7(), "Old", Currency.Pln, 2, FakeClock.DefaultStart, BucketStatus.Archived);
        store.Buckets.Add(archived);

        (await Send(new AddAdjustment(Guid.CreateVersion7(), Money.From(1m, Currency.Pln), Contract.AdjustmentDirection.Increase, Today, archived.Id, null, "Recount", [])))
            .ShouldFailWith(BudgetErrorCodes.BucketArchived, ErrorKind.BusinessRule);
        (await Send(new AddAdjustment(Guid.CreateVersion7(), Money.From(1m, Currency.Eur), Contract.AdjustmentDirection.Increase, Today, cushion.Id, null, "Recount", [])))
            .ShouldFailWith(BudgetErrorCodes.BucketCurrencyMismatch, ErrorKind.BusinessRule);
        (await Send(new AddAdjustment(Guid.CreateVersion7(), Money.From(1m, Currency.Pln), Contract.AdjustmentDirection.Increase, Today, Guid.CreateVersion7(), null, "Recount", [])))
            .ShouldFailWith(BudgetErrorCodes.BucketNotFound, ErrorKind.NotFound);
        (await Send(Adjust(Contract.AdjustmentDirection.Increase, 1m, 1)))
            .ShouldFailWith(BudgetErrorCodes.EntryDateInFuture, ErrorKind.BusinessRule);
        ledger.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARepeat_WritesNothing_AndAnotherDirectionUnderTheSameId_IsIdReused()
    {
        var first = Adjust(Contract.AdjustmentDirection.Increase, 5m, -1);
        (await Send(first)).IsSuccess.ShouldBeTrue();
        store.Journal.AddRange(ledger.Commits[0].Appends.Cast<JournalEntry>());

        (await Send(first)).IsSuccess.ShouldBeTrue();
        (await Send(first with { Direction = Contract.AdjustmentDirection.Decrease }))
            .ShouldFailWith(BudgetErrorCodes.EntryIdReused, ErrorKind.Conflict);
        (await Send(first with { Note = "Another reason" }))
            .ShouldFailWith(BudgetErrorCodes.EntryIdReused, ErrorKind.Conflict);
        ledger.Commits.Count.ShouldBe(1);
    }

    [Fact]
    public void Validator_RequiresTheNote_AsNotEmpty()
    {
        var validator = new AddAdjustmentValidator();

        var missing = validator.Validate(Adjust(Contract.AdjustmentDirection.Increase, 1m, 0) with { Note = null }).Errors.ShouldHaveSingleItem();
        missing.PropertyName.ShouldBe("Note");
        missing.ErrorCode.ShouldBe("NotEmptyValidator");

        var empty = validator.Validate(Adjust(Contract.AdjustmentDirection.Increase, 1m, 0) with { Note = string.Empty }).Errors.ShouldHaveSingleItem();
        empty.PropertyName.ShouldBe("Note");
        empty.ErrorCode.ShouldBe("NotEmptyValidator");
    }

    [Theory]
    [InlineData(" Recount", "NoSurroundingWhiteSpace")]
    [InlineData("Tab\there", "NoControlCharacters")]
    public void Validator_ChecksTheNoteLikeEveryNote(string note, string rule) =>
        new AddAdjustmentValidator().Validate(Adjust(Contract.AdjustmentDirection.Increase, 1m, 0) with { Note = note })
            .Errors.ShouldContain(error => error.PropertyName == "Note" && error.ErrorCode == rule);

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Validator_NeedsExactlyOneTarget(bool bucket, bool unallocated) =>
        new AddAdjustmentValidator().Validate(new AddAdjustment(Guid.CreateVersion7(), Money.From(1m, Currency.Pln), Contract.AdjustmentDirection.Increase, Today,
                bucket ? Guid.CreateVersion7() : null, unallocated ? Currency.Pln : null, "Recount", []))
            .Errors.ShouldContain(error => error.PropertyName == "BucketId" && error.ErrorCode == BucketEntryValidator<AddAdjustment>.ExactlyOneTarget);

    [Fact]
    public void Validator_RejectsAnUndefinedDirection() =>
        new AddAdjustmentValidator().Validate(Adjust((Contract.AdjustmentDirection)7, 1m, 0))
            .Errors.ShouldContain(error => error.PropertyName == "Direction" && error.ErrorCode == "EnumValidator");

    [Fact]
    public void Validator_AcceptsAnOrdinaryAdjustment() =>
        new AddAdjustmentValidator().Validate(new AddAdjustment(Guid.CreateVersion7(), Money.From(1m, Currency.Pln), Contract.AdjustmentDirection.Decrease, Today, null, Currency.Pln, "Two\nlines", ["a"]))
            .IsValid.ShouldBeTrue();

    private AddAdjustment Adjust(Contract.AdjustmentDirection direction, decimal amount, int day) =>
        new(Guid.CreateVersion7(), Money.From(amount, Currency.Pln), direction, Today.AddDays(day), cushion.Id, null, "Counted the cash box", []);

    private Task<FluentResults.Result> Send(AddAdjustment command) =>
        new AddAdjustmentHandler(new BucketEntryWriter(new BudgetStateLoader(ledger, store), ledger, new FakeOwnerContext(), clock)).Handle(command, Cancellation).AsTask();
}
