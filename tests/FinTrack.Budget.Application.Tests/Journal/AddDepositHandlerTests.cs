using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Application.Tests.Journal;

/// <summary>
/// <c>AddDeposit</c> against in-memory fakes: target resolution with <c>Unallocated</c> created in the
/// same commit (D5), idempotency (D4), the amount and date rules (D12, D13), version read first.
/// </summary>
public class AddDepositHandlerTests
{
    // FakeClock.DefaultStart is 2026-01-15 09:30 UTC: in Warsaw that is 2026-01-15.
    private static readonly DateOnly Today = new(2026, 1, 15);

    private readonly CallLog log = new();
    private readonly FakeLedger ledger;
    private readonly FakeBudgetStore store;
    private readonly FakeClock clock = new();
    private readonly Bucket cushion;

    public AddDepositHandlerTests()
    {
        ledger = new FakeLedger(log);
        store = new FakeBudgetStore(log) { Settings = StoredSettings.Of(Currency.Pln, "Europe/Warsaw", "pl-PL") };
        cushion = StoredBuckets.Of(Guid.CreateVersion7(), "Cushion", Currency.Pln, 1, FakeClock.DefaultStart, BucketStatus.Active);
        store.Buckets.Add(cushion);
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DepositIntoABucket_AppendsOneEntry_UnderTheVersionReadFirst()
    {
        ledger.Version = 3;
        var id = Guid.CreateVersion7();

        var result = await Add(new AddDeposit(id, Money.From(250.00m, Currency.Pln), Today, cushion.Id, null, "Salary", ["work"]));

        result.IsSuccess.ShouldBeTrue();
        log.Calls.ShouldBe(["version", "settings", "buckets", "journal", "version", "commit"]);
        var commit = ledger.Commits.ShouldHaveSingleItem();
        commit.ExpectedVersion.ShouldBe(3);
        commit.Saves.ShouldBeEmpty();
        var deposit = commit.Appends.ShouldHaveSingleItem().ShouldBeOfType<Deposit>();
        deposit.Id.ShouldBe(id);
        deposit.OwnerId.ShouldBe(FakeOwnerContext.Owner);
        deposit.BucketId.ShouldBe(cushion.BucketId);
        deposit.Amount.ShouldBe(Money.From(250.00m, Currency.Pln));
        deposit.OccurredOn.ShouldBe(Today);
        deposit.RecordedAt.ShouldBe(FakeClock.DefaultStart);
        deposit.Note.ShouldBe("Salary");
        deposit.Tags.ShouldBe(["work"]);
        deposit.Source.ShouldBe(EntrySource.Manual);
    }

    [Fact]
    public async Task FirstDepositIntoUnallocated_CreatesTheBucketInTheSameCommit()
    {
        var result = await Add(new AddDeposit(Guid.CreateVersion7(), Money.From(10m, Currency.Eur), Today, null, Currency.Eur, null, []));

        result.IsSuccess.ShouldBeTrue();
        var commit = ledger.Commits.ShouldHaveSingleItem();
        var unallocated = commit.Saves.ShouldHaveSingleItem().ShouldBeOfType<Bucket>();
        unallocated.SystemRole.ShouldBe(BucketSystemRole.Unallocated);
        unallocated.Currency.ShouldBe(Currency.Eur);
        unallocated.Version.ShouldBe(0);
        unallocated.OwnerId.ShouldBe(FakeOwnerContext.Owner);
        commit.Appends.ShouldHaveSingleItem().ShouldBeOfType<Deposit>().BucketId.ShouldBe(unallocated.BucketId);
    }

    [Fact]
    public async Task DepositIntoAnExistingUnallocated_UsesIt()
    {
        var unallocated = Bucket.Unallocated(BucketId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, Currency.Pln);
        store.Buckets.Add(unallocated);

        await Add(new AddDeposit(Guid.CreateVersion7(), Money.From(10m, Currency.Pln), Today, null, Currency.Pln, null, []));

        var commit = ledger.Commits.ShouldHaveSingleItem();
        commit.Saves.ShouldBeEmpty();
        commit.Appends.ShouldHaveSingleItem().ShouldBeOfType<Deposit>().BucketId.ShouldBe(unallocated.BucketId);
    }

    [Fact]
    public async Task RepeatedDeposit_WithTheSameContent_SucceedsAndWritesNothing()
    {
        var command = new AddDeposit(Guid.CreateVersion7(), Money.From(10m, Currency.Pln), Today, cushion.Id, null, "Salary", ["work"]);
        await Add(command);
        store.Journal.Add(ledger.Commits[0].Appends[0].ShouldBeOfType<Deposit>());
        clock.Advance(TimeSpan.FromMinutes(5));

        var result = await Add(command);

        result.IsSuccess.ShouldBeTrue();
        ledger.Commits.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RepeatedDepositIntoUnallocated_FindsTheBucketItCreated()
    {
        var command = new AddDeposit(Guid.CreateVersion7(), Money.From(10m, Currency.Usd), Today, null, Currency.Usd, null, []);
        await Add(command);
        store.Buckets.Add(ledger.Commits[0].Saves[0].ShouldBeOfType<Bucket>());
        store.Journal.Add(ledger.Commits[0].Appends[0].ShouldBeOfType<Deposit>());

        (await Add(command)).IsSuccess.ShouldBeTrue();

        ledger.Commits.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(10.01, "2026-01-15", "Salary")]
    [InlineData(10.00, "2026-01-14", "Salary")]
    [InlineData(10.00, "2026-01-15", "Bonus")]
    [InlineData(0, "2026-01-15", "Salary")]
    public async Task RepeatedDeposit_WithOtherContent_IsIdReused(double amount, string occurredOn, string note)
    {
        var id = Guid.CreateVersion7();
        await Add(new AddDeposit(id, Money.From(10m, Currency.Pln), Today, cushion.Id, null, "Salary", []));
        store.Journal.Add(ledger.Commits[0].Appends[0].ShouldBeOfType<Deposit>());

        var result = await Add(new AddDeposit(id, Money.From((decimal)amount, Currency.Pln), DateOnly.Parse(occurredOn, System.Globalization.CultureInfo.InvariantCulture), cushion.Id, null, note, []));

        result.ShouldFailWith(BudgetErrorCodes.EntryIdReused, ErrorKind.Conflict);
        ledger.Commits.Count.ShouldBe(1);
    }

    [Fact]
    public async Task IdThatIsNotVersion7_IsIdInvalid() =>
        (await Add(new AddDeposit(Guid.NewGuid(), Money.From(10m, Currency.Pln), Today, cushion.Id, null, null, [])))
            .ShouldFailWith(ErrorCodes.IdInvalid, ErrorKind.Validation);

    [Fact]
    public async Task BeforeTheFirstRun_IsSettingsNotFound()
    {
        store.Settings = null;

        (await Add(new AddDeposit(Guid.CreateVersion7(), Money.From(10m, Currency.Pln), Today, cushion.Id, null, null, [])))
            .ShouldFailWith(BudgetErrorCodes.SettingsNotFound, ErrorKind.NotFound);
    }

    [Fact]
    public async Task UnknownBucket_IsNotFound() =>
        (await Add(new AddDeposit(Guid.CreateVersion7(), Money.From(10m, Currency.Pln), Today, Guid.CreateVersion7(), null, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketNotFound, ErrorKind.NotFound);

    [Fact]
    public async Task ArchivedBucket_IsRejected()
    {
        var archived = StoredBuckets.Of(Guid.CreateVersion7(), "Old", Currency.Pln, 1, FakeClock.DefaultStart, BucketStatus.Archived);
        store.Buckets.Add(archived);

        (await Add(new AddDeposit(Guid.CreateVersion7(), Money.From(10m, Currency.Pln), Today, archived.Id, null, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketArchived, ErrorKind.BusinessRule);
        log.Calls.ShouldNotContain("commit");
    }

    [Fact]
    public async Task AmountInAnotherCurrency_IsCurrencyMismatch_AndCreatesNoUnallocated()
    {
        (await Add(new AddDeposit(Guid.CreateVersion7(), Money.From(10m, Currency.Eur), Today, cushion.Id, null, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketCurrencyMismatch, ErrorKind.BusinessRule);
        (await Add(new AddDeposit(Guid.CreateVersion7(), Money.From(10m, Currency.Eur), Today, null, Currency.Usd, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketCurrencyMismatch, ErrorKind.BusinessRule);
        log.Calls.ShouldNotContain("commit");
    }

    [Fact]
    public async Task DateAfterTodayInTheOwnersTimeZone_IsRejected()
    {
        // At 10:30 UTC on 15 January it is still the 15th in Warsaw but already 00:30 on the 16th in
        // Kiritimati (UTC+14), so the 16th is allowed only there.
        clock.Advance(TimeSpan.FromHours(1));
        (await Add(new AddDeposit(Guid.CreateVersion7(), Money.From(10m, Currency.Pln), Today.AddDays(1), cushion.Id, null, null, [])))
            .ShouldFailWith(BudgetErrorCodes.EntryDateInFuture, ErrorKind.BusinessRule);

        store.Settings = StoredSettings.Of(Currency.Pln, "Pacific/Kiritimati", "pl-PL");
        (await Add(new AddDeposit(Guid.CreateVersion7(), Money.From(10m, Currency.Pln), Today.AddDays(1), cushion.Id, null, null, []))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ZeroAmount_IsRejected() =>
        (await Add(new AddDeposit(Guid.CreateVersion7(), Money.Zero(Currency.Pln), Today, cushion.Id, null, null, [])))
            .ShouldFailWith(ErrorCodes.MoneyAmountNotPositive, ErrorKind.Validation);

    [Fact]
    public async Task InvestmentBucket_AcceptsDeposits()
    {
        var crypto = Bucket.Create(BucketId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, "Crypto", Currency.Usd, BucketKind.Investment, InvestmentProfile.Crypto, []).Value;
        store.Buckets.Add(crypto);

        (await Add(new AddDeposit(Guid.CreateVersion7(), Money.From(10m, Currency.Usd), Today, crypto.Id, null, null, []))).IsSuccess.ShouldBeTrue();

        var deposit = ledger.Commits.ShouldHaveSingleItem().Appends.ShouldHaveSingleItem().ShouldBeOfType<Deposit>();
        deposit.BucketId.ShouldBe(crypto.BucketId);
        deposit.Amount.ShouldBe(Money.From(10m, Currency.Usd));
    }

    [Theory]
    [InlineData(true, true, "BucketId", "ExactlyOneTarget")]
    [InlineData(false, false, "BucketId", "ExactlyOneTarget")]
    public void Validator_NeedsExactlyOneTarget(bool bucket, bool unallocated, string field, string rule) =>
        new AddDepositValidator().Validate(new AddDeposit(Guid.CreateVersion7(), Money.From(1m, Currency.Pln), Today,
                bucket ? Guid.CreateVersion7() : null, unallocated ? Currency.Pln : null, null, []))
            .Errors.ShouldContain(error => error.PropertyName == field && error.ErrorCode == rule);

    [Theory]
    [InlineData(" Salary", "Note", "NoSurroundingWhiteSpace")]
    [InlineData("Tab\there", "Note", "NoControlCharacters")]
    [InlineData("", "Note", "NotEmptyValidator")]
    public void Validator_ChecksTheNote(string note, string field, string rule) =>
        new AddDepositValidator().Validate(new AddDeposit(Guid.CreateVersion7(), Money.From(1m, Currency.Pln), Today, Guid.CreateVersion7(), null, note, []))
            .Errors.ShouldContain(error => error.PropertyName == field && error.ErrorCode == rule);

    [Fact]
    public void Validator_ChecksTheTags()
    {
        var validator = new AddDepositValidator();
        AddDeposit With(params string[] tags) => new(Guid.CreateVersion7(), Money.From(1m, Currency.Pln), Today, Guid.CreateVersion7(), null, null, tags);

        validator.Validate(With("car", "Car")).Errors.ShouldContain(error => error.PropertyName == "Tags" && error.ErrorCode == EntryRules.DistinctTags);
        validator.Validate(With([.. Enumerable.Range(1, 11).Select(number => $"t{number}")])).Errors.ShouldContain(error => error.PropertyName == "Tags" && error.ErrorCode == EntryRules.TooManyTags);
        validator.Validate(With("ok", " bad")).Errors.ShouldContain(error => error.PropertyName == "Tags[1]" && error.ErrorCode == "NoSurroundingWhiteSpace");
        validator.Validate(With("ok", "line\nbreak")).Errors.ShouldContain(error => error.PropertyName == "Tags[1]" && error.ErrorCode == "NoControlCharacters");
    }

    [Fact]
    public void Validator_AcceptsAnOrdinaryDeposit() =>
        new AddDepositValidator().Validate(new AddDeposit(Guid.CreateVersion7(), Money.From(1m, Currency.Pln), Today, null, Currency.Pln, "Two\nlines", ["a", "b"]))
            .IsValid.ShouldBeTrue();

    private Task<FluentResults.Result> Add(AddDeposit command) =>
        new AddDepositHandler(new BucketEntryWriter(new BudgetStateLoader(ledger, store), ledger, new FakeOwnerContext(), clock)).Handle(command, Cancellation).AsTask();
}
