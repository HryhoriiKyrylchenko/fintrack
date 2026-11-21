using FinTrack.Budget.Application.Loans;
using FinTrack.Budget.Contracts.V1.Loans;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;
using Model = FinTrack.Budget.Domain.Loans;

namespace FinTrack.Budget.Application.Tests.Loans;

/// <summary>
/// <c>GetLoans</c> (plan 1b B19) against in-memory fakes: the loans replayed for today, open ones by default and closed ones
/// on request, in the order of their borrows, with every field of the V1 contract; nothing is written.
/// </summary>
public class GetLoansHandlerTests
{
    private static readonly DateOnly Today = new(2026, 1, 15);

    private readonly CallLog log = new();
    private readonly FakeLedger ledger;
    private readonly FakeBudgetStore store;
    private readonly FakeClock clock = new();
    private readonly Bucket cushion;
    private readonly Bucket car;
    private int recorded;

    public GetLoansHandlerTests()
    {
        ledger = new FakeLedger(log);
        store = new FakeBudgetStore(log) { Settings = StoredSettings.Of(Currency.Pln, "Europe/Warsaw", "pl-PL") };
        cushion = StoredBuckets.Of(Guid.CreateVersion7(), "Cushion", Currency.Pln, 1, FakeClock.DefaultStart, BucketStatus.Active);
        car = StoredBuckets.Of(Guid.CreateVersion7(), "Car", Currency.Pln, 1, FakeClock.DefaultStart, BucketStatus.Active);
        store.Buckets.AddRange([cushion, car]);
        Add(Deposit.Create(NewId(), FakeOwnerContext.Owner, cushion.BucketId, Pln(1000m), Today.AddDays(-30), Today, EntryInfo.None, NextRecordedAt()).Value);
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BeforeTheFirstRun_IsSettingsNotFound()
    {
        store.Settings = null;

        (await Loans(new GetLoans())).ShouldFailWith(BudgetErrorCodes.SettingsNotFound, ErrorKind.NotFound);
    }

    [Fact]
    public async Task WithoutBorrows_ThereAreNoLoans() =>
        (await Loans(new GetLoans(IncludeClosed: true))).Value.ShouldBeEmpty();

    [Fact]
    public async Task AnOpenLoan_CarriesEveryField()
    {
        var borrow = Borrow(300m, car, -10, "Car repair");
        Repay(borrow, 100m, -5);

        var loan = (await Loans(new GetLoans())).Value.ShouldHaveSingleItem();

        loan.ShouldBe(new LoanDto(borrow.Id, cushion.Id, car.Id, Pln(300m), Today.AddDays(-10), Pln(100m), Pln(200m), LoanStatus.Open, ClosedOn: null, "Car repair"));
    }

    [Fact]
    public async Task ALoanToOutside_HasNoDestination()
    {
        Borrow(10m, null, -3, null);

        (await Loans(new GetLoans())).Value.ShouldHaveSingleItem().ToBucketId.ShouldBeNull();
    }

    [Fact]
    public async Task ClosedLoans_AreListedOnlyOnRequest_WithTheDayTheyClosed()
    {
        var open = Borrow(50m, car, -9, null);
        var closed = Borrow(40m, null, -8, null);
        Repay(closed, 15m, -6);
        Repay(closed, 25m, -4);

        (await Loans(new GetLoans())).Value.Select(loan => loan.Id).ShouldBe([open.Id]);

        var all = (await Loans(new GetLoans(IncludeClosed: true))).Value;
        all.Select(loan => loan.Id).ShouldBe([open.Id, closed.Id]);
        var paid = all.Single(loan => loan.Id == closed.Id);
        paid.Status.ShouldBe(LoanStatus.Closed);
        paid.ClosedOn.ShouldBe(Today.AddDays(-4));
        paid.Outstanding.ShouldBe(Money.Zero(Currency.Pln));
        paid.Repaid.ShouldBe(Pln(40m));
    }

    [Fact]
    public async Task LoansAreInTheReplayOrderOfTheirBorrows_NotTheOrderTheyWereRecorded()
    {
        var recordedFirst = Borrow(1m, null, -2, null);
        var datedEarlier = Borrow(1m, null, -7, null);

        (await Loans(new GetLoans())).Value.Select(loan => loan.Id).ShouldBe([datedEarlier.Id, recordedFirst.Id]);
    }

    [Fact]
    public async Task AReversedBorrow_IsNoLoan_AndAReversedRepayment_DoesNotCount()
    {
        var reversedBorrow = Borrow(10m, null, -9, null);
        Reverse(reversedBorrow);
        var loan = Borrow(30m, null, -8, null);
        Reverse(Repay(loan, 30m, -4));

        var listed = (await Loans(new GetLoans(IncludeClosed: true))).Value.ShouldHaveSingleItem();
        listed.Id.ShouldBe(loan.Id);
        listed.Status.ShouldBe(LoanStatus.Open);
        listed.Repaid.ShouldBe(Money.Zero(Currency.Pln));
    }

    [Fact]
    public async Task ReadsTheStateThroughTheLoader_AndWritesNothing()
    {
        Borrow(10m, null, -1, null);

        (await Loans(new GetLoans())).IsSuccess.ShouldBeTrue();

        log.Calls.ShouldBe(["version", "settings", "buckets", "journal", "version"]);
        ledger.Commits.ShouldBeEmpty();
    }

    // The contract's copy of the status (Contracts may not reference Domain): its names are the JSON names. Both members
    // are mapped to their namesakes in the tests above (Open and Closed).
    [Fact]
    public void Statuses_MatchTheDomainsByName() =>
        Enum.GetNames<LoanStatus>().ShouldBe(Enum.GetNames<Model.LoanStatus>());

    private Task<FluentResults.Result<IReadOnlyList<LoanDto>>> Loans(GetLoans query) =>
        new GetLoansHandler(new BudgetStateLoader(ledger, store), clock).Handle(query, Cancellation).AsTask();

    private Borrow Borrow(decimal principal, Bucket? to, int day, string? note) =>
        Add(Domain.Journal.Borrow.Create(NewId(), FakeOwnerContext.Owner, cushion.BucketId, Pln(principal), to?.BucketId, Today.AddDays(day), Today,
            new EntryInfo(note, [], EntrySource.Manual), NextRecordedAt()).Value);

    private Repayment Repay(Borrow borrow, decimal amount, int day) =>
        Add(Repayment.Create(NewId(), FakeOwnerContext.Owner, borrow.LoanId, Pln(amount), null, Today.AddDays(day), Today, EntryInfo.None, NextRecordedAt()).Value);

    private Reversal Reverse(JournalEntry entry) =>
        Add(Reversal.Create(NewId(), FakeOwnerContext.Owner, entry.EntryId, Today, Today, EntryInfo.None, NextRecordedAt()).Value);

    private T Add<T>(T entry)
        where T : JournalEntry
    {
        store.Journal.Add(entry);
        return entry;
    }

    // Distinct, increasing times, so the order never depends on random ID bits.
    private DateTimeOffset NextRecordedAt() => FakeClock.DefaultStart.AddDays(-40).AddSeconds(++recorded);

    private static JournalEntryId NewId() => JournalEntryId.From(Guid.CreateVersion7());

    private static Money Pln(decimal amount) => Money.From(amount, Currency.Pln);
}
