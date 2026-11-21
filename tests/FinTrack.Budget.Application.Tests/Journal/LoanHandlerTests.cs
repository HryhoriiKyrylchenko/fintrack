using FinTrack.Budget.Application.Buckets;
using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.Loans;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Application.Tests.Journal;

/// <summary>
/// <c>AddBorrow</c>, <c>AddRepayment</c> (plan 1b B2 to B5) and the loan rules of <c>ReverseEntry</c> and <c>ArchiveBucket</c>
/// (B5, B17) against in-memory fakes: the lender, destination and source rules, invariants 1 and 4 with the first day they
/// break, a loan that opens, closes and reopens, and repeats.
/// </summary>
public class LoanHandlerTests
{
    // FakeClock.DefaultStart is 2026-01-15 09:30 UTC: in Warsaw that is 2026-01-15.
    private static readonly DateOnly Today = new(2026, 1, 15);

    private readonly CallLog log = new();
    private readonly FakeLedger ledger;
    private readonly FakeBudgetStore store;
    private readonly FakeClock clock = new();
    private readonly Bucket cushion;
    private readonly Bucket holiday;

    public LoanHandlerTests()
    {
        ledger = new FakeLedger(log);
        store = new FakeBudgetStore(log) { Settings = StoredSettings.Of(Currency.Pln, "Europe/Warsaw", "pl-PL") };
        cushion = StoredBuckets.Of(Guid.CreateVersion7(), "Cushion", Currency.Pln, 1, FakeClock.DefaultStart, BucketStatus.Active);
        holiday = StoredBuckets.Of(Guid.CreateVersion7(), "Holiday", Currency.Pln, 1, FakeClock.DefaultStart, BucketStatus.Active);
        store.Buckets.AddRange([cushion, holiday]);
        store.Journal.Add(Deposit.Create(JournalEntryId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, cushion.BucketId, Pln(1000m),
            Today.AddDays(-20), Today, EntryInfo.None, FakeClock.DefaultStart.AddDays(-20)).Value);
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ABorrowIntoABucket_AppendsOneEntry_UnderTheVersionReadFirst()
    {
        ledger.Version = 3;
        var id = Guid.CreateVersion7();

        var result = await Send(new AddBorrow(id, cushion.Id, Pln(250.5m), Today.AddDays(-2), holiday.Id, null, "Car repair", ["car"]));

        result.IsSuccess.ShouldBeTrue();
        log.Calls.ShouldBe(["version", "settings", "buckets", "journal", "version", "commit"]);
        var commit = ledger.Commits.ShouldHaveSingleItem();
        commit.ExpectedVersion.ShouldBe(3);
        commit.Saves.ShouldBeEmpty();
        var borrow = commit.Appends.ShouldHaveSingleItem().ShouldBeOfType<Borrow>();
        borrow.Id.ShouldBe(id);
        borrow.LoanId.ShouldBe(LoanId.From(id));
        borrow.LenderBucketId.ShouldBe(cushion.BucketId);
        borrow.Principal.ShouldBe(Pln(250.50m));
        borrow.ToBucketId.ShouldBe(holiday.BucketId);
        borrow.OccurredOn.ShouldBe(Today.AddDays(-2));
        borrow.Note.ShouldBe("Car repair");
        borrow.Tags.ShouldBe(["car"]);
    }

    [Fact]
    public async Task ABorrowToOutside_HasNoDestination_AndOneIntoUnallocated_CreatesIt()
    {
        (await Send(new AddBorrow(Guid.CreateVersion7(), cushion.Id, Pln(10m), Today, null, null, null, []))).IsSuccess.ShouldBeTrue();
        ledger.Commits[0].Appends.ShouldHaveSingleItem().ShouldBeOfType<Borrow>().ToBucketId.ShouldBeNull();

        (await Send(new AddBorrow(Guid.CreateVersion7(), cushion.Id, Pln(10m), Today, null, Currency.Pln, null, []))).IsSuccess.ShouldBeTrue();
        var unallocated = ledger.Commits[1].Saves.ShouldHaveSingleItem().ShouldBeOfType<Bucket>();
        unallocated.SystemRole.ShouldBe(BucketSystemRole.Unallocated);
        ledger.Commits[1].Appends.ShouldHaveSingleItem().ShouldBeOfType<Borrow>().ToBucketId.ShouldBe(unallocated.BucketId);
    }

    [Fact]
    public async Task ABorrowAboveTheLendersAvailable_IsInsufficientFunds_WithTheDay()
    {
        (await Send(new AddBorrow(Guid.CreateVersion7(), cushion.Id, Pln(1000.01m), Today.AddDays(-3), holiday.Id, null, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketInsufficientFunds, ErrorKind.BusinessRule).Parameters["date"].ShouldBe("2026-01-12");
        (await Send(new AddBorrow(Guid.CreateVersion7(), cushion.Id, Pln(1m), Today.AddDays(-30), holiday.Id, null, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketInsufficientFunds, ErrorKind.BusinessRule).Parameters["date"].ShouldBe("2025-12-16");
        ledger.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheLenderMustBeAnActiveSavingsBucketOfTheUser_InThePrincipalsCurrency()
    {
        var crypto = StoredBuckets.Of(Guid.CreateVersion7(), "Crypto", Currency.Pln, FakeClock.DefaultStart, BucketStatus.Active, BucketKind.Investment, InvestmentProfile.Crypto, BucketSystemRole.None);
        var unallocated = Bucket.Unallocated(BucketId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, Currency.Pln);
        var old = StoredBuckets.Of(Guid.CreateVersion7(), "Old", Currency.Pln, 2, FakeClock.DefaultStart, BucketStatus.Archived);
        store.Buckets.AddRange([crypto, unallocated, old]);

        (await Borrow(crypto.Id, Pln(1m), null)).ShouldFailWith(BudgetErrorCodes.BucketBorrowNotAllowed, ErrorKind.BusinessRule);
        (await Borrow(unallocated.Id, Pln(1m), null)).ShouldFailWith(BudgetErrorCodes.BucketSystemBucket, ErrorKind.BusinessRule);
        (await Borrow(old.Id, Pln(1m), null)).ShouldFailWith(BudgetErrorCodes.BucketArchived, ErrorKind.BusinessRule);
        (await Borrow(cushion.Id, Money.From(1m, Currency.Eur), null)).ShouldFailWith(BudgetErrorCodes.BucketCurrencyMismatch, ErrorKind.BusinessRule);
        (await Borrow(Guid.CreateVersion7(), Pln(1m), null)).ShouldFailWith(BudgetErrorCodes.BucketNotFound, ErrorKind.NotFound);
        ledger.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheDestinationMustBeAnotherActiveBucketOfTheLendersCurrency()
    {
        var euro = StoredBuckets.Of(Guid.CreateVersion7(), "Euro", Currency.Eur, 1, FakeClock.DefaultStart, BucketStatus.Active);
        var old = StoredBuckets.Of(Guid.CreateVersion7(), "Old", Currency.Pln, 2, FakeClock.DefaultStart, BucketStatus.Archived);
        store.Buckets.AddRange([euro, old]);

        (await Borrow(cushion.Id, Pln(1m), cushion.Id)).ShouldFailWith(BudgetErrorCodes.BorrowSameBucket, ErrorKind.Validation);
        (await Borrow(cushion.Id, Pln(1m), euro.Id)).ShouldFailWith(BudgetErrorCodes.BucketCurrencyMismatch, ErrorKind.BusinessRule);
        (await Borrow(cushion.Id, Pln(1m), old.Id)).ShouldFailWith(BudgetErrorCodes.BucketArchived, ErrorKind.BusinessRule);
        (await Borrow(cushion.Id, Pln(1m), Guid.CreateVersion7())).ShouldFailWith(BudgetErrorCodes.BucketNotFound, ErrorKind.NotFound);
        (await Send(new AddBorrow(Guid.CreateVersion7(), cushion.Id, Pln(1m), Today, null, Currency.Eur, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketCurrencyMismatch, ErrorKind.BusinessRule);
        ledger.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task ALoan_IsRepaidInParts_Closes_AndIsClosedForMore()
    {
        var loan = await Lend(100m, -10);

        (await Repay(loan, 30m, holiday.Id, -8)).IsSuccess.ShouldBeTrue();
        (await Repay(loan, 70m, null, -5)).IsSuccess.ShouldBeTrue();

        var state = State();
        state.FindLoan(loan)!.Status.ShouldBe(LoanStatus.Closed);
        state.FindLoan(loan)!.ClosedOn.ShouldBe(Today.AddDays(-5));
        state.BalanceOf(cushion.BucketId).Available.ShouldBe(Pln(1000m));
        state.BalanceOf(holiday.BucketId).NetContributed.ShouldBe(Pln(70m));
        state.BalanceOf(cushion.BucketId).Deposited.ShouldBe(Pln(1000m));

        (await Repay(loan, 1m, null, 0)).ShouldFailWith(BudgetErrorCodes.LoanClosed, ErrorKind.BusinessRule);
    }

    [Fact]
    public async Task ARepaymentAboveTheOutstandingPrincipal_OrBeforeTheBorrow_IsRefusedWithTheDay_AndWritesNothing()
    {
        var loan = await Lend(100m, -10);
        var commits = ledger.Commits.Count;

        (await Repay(loan, 100.01m, null, -2)).ShouldFailWith(BudgetErrorCodes.LoanRepaymentExceedsOutstanding, ErrorKind.BusinessRule)
            .Parameters.ShouldBe(new Dictionary<string, object> { ["date"] = "2026-01-13" });
        (await Repay(loan, 1m, null, -12)).ShouldFailWith(BudgetErrorCodes.LoanRepaymentExceedsOutstanding, ErrorKind.BusinessRule)
            .Parameters["date"].ShouldBe("2026-01-03");
        ledger.Commits.Count.ShouldBe(commits);
    }

    [Fact]
    public async Task ABackdatedRepayment_ThatLaterRepaymentsWouldExceed_IsRefusedOnTheLaterDay()
    {
        var loan = await Lend(100m, -10);
        (await Repay(loan, 80m, null, -3)).IsSuccess.ShouldBeTrue();

        (await Repay(loan, 30m, null, -6)).ShouldFailWith(BudgetErrorCodes.LoanRepaymentExceedsOutstanding, ErrorKind.BusinessRule)
            .Parameters["date"].ShouldBe("2026-01-12");
    }

    [Fact]
    public async Task OverRepayment_IsReportedBeforeAnOverdraftOfTheSource()
    {
        var loan = await Lend(100m, -10, toOutside: true);

        // Holiday holds nothing and the loan owes 100: both invariants would break; invariant 4 is reported (B4).
        (await Repay(loan, 150m, holiday.Id, -1)).ShouldFailWith(BudgetErrorCodes.LoanRepaymentExceedsOutstanding, ErrorKind.BusinessRule);
        (await Repay(loan, 50m, holiday.Id, -1)).ShouldFailWith(BudgetErrorCodes.BucketInsufficientFunds, ErrorKind.BusinessRule);
    }

    [Fact]
    public async Task TheLoanMustExist_AndTheSourceBeAnotherActiveBucketOfItsCurrency()
    {
        var loan = await Lend(100m, -10);
        var euro = StoredBuckets.Of(Guid.CreateVersion7(), "Euro", Currency.Eur, 1, FakeClock.DefaultStart, BucketStatus.Active);
        store.Buckets.Add(euro);
        var commits = ledger.Commits.Count;

        (await Repay(LoanId.From(Guid.CreateVersion7()), 1m, null, 0)).ShouldFailWith(BudgetErrorCodes.LoanNotFound, ErrorKind.NotFound);
        (await Repay(LoanId.From(store.Journal[0].Id), 1m, null, 0)).ShouldFailWith(BudgetErrorCodes.LoanNotFound, ErrorKind.NotFound);
        (await Repay(loan, 1m, cushion.Id, 0)).ShouldFailWith(BudgetErrorCodes.RepaymentFromLender, ErrorKind.BusinessRule);
        (await Send(new AddRepayment(Guid.CreateVersion7(), loan.Value, Money.From(1m, Currency.Eur), Today, euro.Id, null, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketCurrencyMismatch, ErrorKind.BusinessRule);
        (await Repay(loan, 1m, euro.Id, 0)).ShouldFailWith(BudgetErrorCodes.BucketCurrencyMismatch, ErrorKind.BusinessRule);
        (await Repay(loan, 1m, Guid.CreateVersion7(), 0)).ShouldFailWith(BudgetErrorCodes.BucketNotFound, ErrorKind.NotFound);
        ledger.Commits.Count.ShouldBe(commits);
    }

    [Fact]
    public async Task ARepaymentFromUnallocated_PaysFromIt()
    {
        var loan = await Lend(100m, -10, toUnallocated: true);

        (await Send(new AddRepayment(Guid.CreateVersion7(), loan.Value, Pln(40m), Today, null, Currency.Pln, null, []))).IsSuccess.ShouldBeTrue();

        var unallocated = store.Buckets.Single(bucket => bucket.SystemRole == BucketSystemRole.Unallocated);
        StoreCommits();
        State().BalanceOf(unallocated.BucketId).NetContributed.ShouldBe(Pln(60m));
    }

    [Fact]
    public async Task ARepeatOfTheRepaymentThatClosedTheLoan_WritesNothing_InsteadOfLoanClosed()
    {
        var loan = await Lend(100m, -10);
        var last = new AddRepayment(Guid.CreateVersion7(), loan.Value, Pln(100m), Today, null, null, null, []);
        (await Send(last)).IsSuccess.ShouldBeTrue();
        StoreCommits();
        var commits = ledger.Commits.Count;

        (await Send(last)).IsSuccess.ShouldBeTrue();
        (await Send(last with { Amount = Pln(99m) })).ShouldFailWith(BudgetErrorCodes.EntryIdReused, ErrorKind.Conflict);
        ledger.Commits.Count.ShouldBe(commits);
    }

    // Found by the model test: the loan is checked after the repeat, so a repayment repeated after it and its borrow were
    // reversed is still recognised (invariant 13), while a new repayment of that loan is loan.not_found.
    [Fact]
    public async Task ARepeatedRepayment_AfterItAndItsBorrowWereReversed_WritesNothing()
    {
        var loan = await Lend(100m, -10);
        var repayment = new AddRepayment(Guid.CreateVersion7(), loan.Value, Pln(30m), Today.AddDays(-5), null, null, null, []);
        (await Send(repayment)).IsSuccess.ShouldBeTrue();
        StoreCommits();
        (await Reverse(repayment.Id)).IsSuccess.ShouldBeTrue();
        StoreCommits();
        (await Reverse(loan.Value)).IsSuccess.ShouldBeTrue();
        StoreCommits();
        var commits = ledger.Commits.Count;

        (await Send(repayment)).IsSuccess.ShouldBeTrue();
        (await Send(repayment with { Amount = Pln(31m) })).ShouldFailWith(BudgetErrorCodes.EntryIdReused, ErrorKind.Conflict);
        (await Repay(loan, 1m, null, 0)).ShouldFailWith(BudgetErrorCodes.LoanNotFound, ErrorKind.NotFound);
        ledger.Commits.Count.ShouldBe(commits);
    }

    [Fact]
    public async Task ARepeatedBorrow_WritesNothing_AndAnotherDestination_IsIdReused()
    {
        var borrow = new AddBorrow(Guid.CreateVersion7(), cushion.Id, Pln(10m), Today, holiday.Id, null, null, []);
        (await Send(borrow)).IsSuccess.ShouldBeTrue();
        StoreCommits();

        (await Send(borrow)).IsSuccess.ShouldBeTrue();
        (await Send(borrow with { ToBucketId = null })).ShouldFailWith(BudgetErrorCodes.EntryIdReused, ErrorKind.Conflict);
        ledger.Commits.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ReversingABorrowWithRepayments_HasDependents_UntilTheRepaymentsAreReversed()
    {
        var loan = await Lend(100m, -10);
        (await Repay(loan, 30m, null, -5)).IsSuccess.ShouldBeTrue();
        StoreCommits();
        var repayment = store.Journal.OfType<Repayment>().Single();

        (await Reverse(loan.Value)).ShouldFailWith(BudgetErrorCodes.EntryHasDependents, ErrorKind.BusinessRule);

        (await Reverse(repayment.Id)).IsSuccess.ShouldBeTrue();
        StoreCommits();
        State().FindLoan(loan)!.Outstanding.ShouldBe(Pln(100m));
        (await Reverse(loan.Value)).IsSuccess.ShouldBeTrue();
        StoreCommits();
        State().Loans.ShouldBeEmpty();
        State().BalanceOf(cushion.BucketId).Available.ShouldBe(Pln(1000m));
    }

    [Fact]
    public async Task ReversingTheLastRepayment_ReopensTheLoan()
    {
        var loan = await Lend(100m, -10);
        (await Repay(loan, 100m, null, -5)).IsSuccess.ShouldBeTrue();
        StoreCommits();

        (await Reverse(store.Journal.OfType<Repayment>().Single().Id)).IsSuccess.ShouldBeTrue();
        StoreCommits();

        State().FindLoan(loan)!.Status.ShouldBe(LoanStatus.Open);
    }

    [Fact]
    public async Task ReversingABorrowThatFundedASpend_IsInsufficientFunds()
    {
        var loan = await Lend(100m, -10);
        store.Journal.Add(Spend.Create(JournalEntryId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, holiday.BucketId, Pln(60m), Today.AddDays(-4), Today,
            EntryInfo.None, FakeClock.DefaultStart.AddDays(-4)).Value);

        (await Reverse(loan.Value)).ShouldFailWith(BudgetErrorCodes.BucketInsufficientFunds, ErrorKind.BusinessRule).Parameters["date"].ShouldBe("2026-01-11");
    }

    [Fact]
    public async Task ReversingARepaymentWhoseLenderIsArchived_IsBucketArchived()
    {
        var loan = await Lend(100m, -10);
        (await Repay(loan, 100m, holiday.Id, -5)).IsSuccess.ShouldBeTrue();
        StoreCommits();
        // The loan is closed and the lender empty... except for its own deposit: spend it, then archive the lender.
        store.Journal.Add(Spend.Create(JournalEntryId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, cushion.BucketId, Pln(1000m), Today.AddDays(-1), Today,
            EntryInfo.None, FakeClock.DefaultStart.AddDays(-1)).Value);
        (await Archive(cushion.Id)).IsSuccess.ShouldBeTrue();
        store.Buckets[0] = ledger.Commits[^1].Saves.ShouldHaveSingleItem().ShouldBeOfType<Bucket>();

        (await Reverse(store.Journal.OfType<Repayment>().Single().Id)).ShouldFailWith(BudgetErrorCodes.BucketArchived, ErrorKind.BusinessRule);
    }

    [Fact]
    public async Task ReversingABorrowIntoAnArchivedBucket_IsBucketArchived()
    {
        var old = StoredBuckets.Of(Guid.CreateVersion7(), "Old", Currency.Pln, 2, FakeClock.DefaultStart, BucketStatus.Archived);
        store.Buckets.Add(old);
        var borrow = Domain.Journal.Borrow.Create(JournalEntryId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, cushion.BucketId, Pln(1m), old.BucketId,
            Today.AddDays(-3), Today, EntryInfo.None, FakeClock.DefaultStart.AddDays(-3)).Value;
        store.Journal.Add(borrow);

        (await Reverse(borrow.Id)).ShouldFailWith(BudgetErrorCodes.BucketArchived, ErrorKind.BusinessRule);
    }

    [Fact]
    public async Task ALenderWithAnOpenLoan_IsNotArchived_AndWithAClosedOneOnlyWhenEmpty()
    {
        var loan = await Lend(100m, -10, toOutside: true);
        store.Journal.Add(Spend.Create(JournalEntryId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, cushion.BucketId, Pln(900m), Today.AddDays(-9), Today,
            EntryInfo.None, FakeClock.DefaultStart.AddDays(-9)).Value);
        State().BalanceOf(cushion.BucketId).Available.ShouldBe(Money.Zero(Currency.Pln));

        (await Archive(cushion.Id)).ShouldFailWith(BudgetErrorCodes.BucketHasOpenLoan, ErrorKind.BusinessRule);

        (await Repay(loan, 100m, null, -2)).IsSuccess.ShouldBeTrue();
        StoreCommits();
        (await Archive(cushion.Id)).ShouldFailWith(BudgetErrorCodes.BucketNotEmpty, ErrorKind.BusinessRule);
    }

    [Fact]
    public void Validators_AllowAtMostOneDestinationOrSource()
    {
        new AddBorrowValidator().Validate(new AddBorrow(Guid.CreateVersion7(), cushion.Id, Pln(1m), Today, holiday.Id, Currency.Pln, null, []))
            .Errors.ShouldContain(error => error.PropertyName == "ToBucketId" && error.ErrorCode == EntryRules.AtMostOneTarget);
        new AddBorrowValidator().Validate(new AddBorrow(Guid.CreateVersion7(), cushion.Id, Pln(1m), Today, null, null, null, [])).IsValid.ShouldBeTrue();
        new AddRepaymentValidator().Validate(new AddRepayment(Guid.CreateVersion7(), Guid.CreateVersion7(), Pln(1m), Today, holiday.Id, Currency.Pln, null, []))
            .Errors.ShouldContain(error => error.PropertyName == "FromBucketId" && error.ErrorCode == EntryRules.AtMostOneTarget);
        new AddRepaymentValidator().Validate(new AddRepayment(Guid.CreateVersion7(), Guid.CreateVersion7(), Pln(1m), Today, null, Currency.Pln, "Paid", ["a"]))
            .IsValid.ShouldBeTrue();
        new AddRepaymentValidator().Validate(new AddRepayment(Guid.CreateVersion7(), Guid.CreateVersion7(), Pln(1m), Today, null, null, " x", []))
            .Errors.ShouldContain(error => error.PropertyName == "Note" && error.ErrorCode == "NoSurroundingWhiteSpace");
        new AddRepaymentValidator().Validate(new AddRepayment(Guid.CreateVersion7(), Guid.Empty, Pln(1m), Today, null, null, null, []))
            .Errors.ShouldContain(error => error.PropertyName == "LoanId" && error.ErrorCode == "NotEmptyValidator");
    }

    // A loan from Cushion, into Holiday unless told otherwise; the commit is stored so later commands see it.
    private async Task<LoanId> Lend(decimal principal, int day, bool toOutside = false, bool toUnallocated = false)
    {
        var id = Guid.CreateVersion7();
        var result = await Send(new AddBorrow(id, cushion.Id, Pln(principal), Today.AddDays(day), toOutside || toUnallocated ? null : holiday.Id,
            toUnallocated ? Currency.Pln : null, null, []));
        result.IsSuccess.ShouldBeTrue();
        StoreCommits();
        return LoanId.From(id);
    }

    private int stored;

    private void StoreCommits()
    {
        foreach (var commit in ledger.Commits.Skip(stored))
        {
            store.Journal.AddRange(commit.Appends.Cast<JournalEntry>());
            foreach (var bucket in commit.Saves.OfType<Bucket>())
            {
                var index = store.Buckets.FindIndex(existing => existing.Id == bucket.Id);
                if (index < 0)
                {
                    store.Buckets.Add(bucket);
                }
                else
                {
                    store.Buckets[index] = bucket;
                }
            }
        }

        stored = ledger.Commits.Count;
    }

    private BudgetState State() => BudgetState.Replay(store.Buckets, store.Journal, Today);

    private Task<FluentResults.Result> Borrow(Guid lender, Money principal, Guid? to) =>
        Send(new AddBorrow(Guid.CreateVersion7(), lender, principal, Today, to, null, null, []));

    // A repayment; when accepted, its commit is stored so later commands see it.
    private async Task<FluentResults.Result> Repay(LoanId loan, decimal amount, Guid? from, int day)
    {
        var result = await Send(new AddRepayment(Guid.CreateVersion7(), loan.Value, Pln(amount), Today.AddDays(day), from, null, null, []));
        StoreCommits();
        return result;
    }

    private Task<FluentResults.Result> Send(AddBorrow command) =>
        new AddBorrowHandler(Writer()).Handle(command, Cancellation).AsTask();

    private Task<FluentResults.Result> Send(AddRepayment command) =>
        new AddRepaymentHandler(Writer()).Handle(command, Cancellation).AsTask();

    private Task<FluentResults.Result> Reverse(Guid target) =>
        new ReverseEntryHandler(Writer(), new JournalEntryDependencies()).Handle(new ReverseEntry(Guid.CreateVersion7(), target, Today, null, []), Cancellation).AsTask();

    private Task<FluentResults.Result> Archive(Guid bucket) =>
        new ArchiveBucketHandler(new BudgetStateLoader(ledger, store), ledger, clock).Handle(new ArchiveBucket(bucket), Cancellation).AsTask();

    private JournalWriter Writer() => new(new BudgetStateLoader(ledger, store), ledger, new FakeOwnerContext(), clock);

    private static Money Pln(decimal amount) => Money.From(amount, Currency.Pln);
}
