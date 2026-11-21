using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.Loans;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Domain.Tests.Journal;

/// <summary>
/// <c>Borrow</c> and <c>Repayment</c> (ARCHITECTURE 4.2, plan 1b B2): a lender, a principal above zero and an optional
/// destination; a loan, an amount above zero and an optional source; the date rule of every entry.
/// </summary>
public class BorrowAndRepaymentTests
{
    private static readonly OwnerId Owner = OwnerId.From(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly BucketId Cushion = BucketId.From(Guid.Parse("01900000-0000-7000-8000-000000000001"));
    private static readonly BucketId Holiday = BucketId.From(Guid.Parse("01900000-0000-7000-8000-000000000002"));
    private static readonly JournalEntryId EntryId = JournalEntryId.From(Guid.Parse("01900000-0000-7000-8000-0000000000aa"));
    private static readonly LoanId Loan = LoanId.From(Guid.Parse("01900000-0000-7000-8000-0000000000bb"));
    private static readonly DateOnly Today = new(2026, 1, 15);
    private static readonly EntryInfo Info = new("Car repair", ["car"], EntrySource.Manual);

    [Fact]
    public void Borrow_HoldsTheValues_AndItsIdIsTheLoans()
    {
        var borrow = Borrow.Create(EntryId, Owner, Cushion, Money.From(500.5m, Currency.Pln), Holiday, Today.AddDays(-3), Today, Info, FakeClock.DefaultStart).Value;

        borrow.Id.ShouldBe(EntryId.Value);
        borrow.LoanId.ShouldBe(LoanId.From(EntryId.Value));
        borrow.OwnerId.ShouldBe(Owner);
        borrow.LenderBucketId.ShouldBe(Cushion);
        borrow.Principal.ShouldBe(Money.From(500.50m, Currency.Pln));
        borrow.ToBucketId.ShouldBe(Holiday);
        borrow.OccurredOn.ShouldBe(Today.AddDays(-3));
        borrow.RecordedAt.ShouldBe(FakeClock.DefaultStart);
        borrow.Note.ShouldBe("Car repair");
        borrow.Tags.ShouldBe(["car"]);
        borrow.SchemaVersion.ShouldBe(Borrow.CurrentSchemaVersion);
    }

    [Fact]
    public void Borrow_ToOutside_HasNoDestination() =>
        Borrow.Create(EntryId, Owner, Cushion, Money.From(1m, Currency.Pln), null, Today, Today, EntryInfo.None, FakeClock.DefaultStart).Value
            .ToBucketId.ShouldBeNull();

    [Fact]
    public void Borrow_BreakingEveryRule_ReportsEachOne()
    {
        var result = Borrow.Create(EntryId, Owner, Cushion, Money.Zero(Currency.Pln), Cushion, Today.AddDays(1), Today, EntryInfo.None, FakeClock.DefaultStart);

        result.Errors.Cast<AppError>().Select(error => error.Code)
            .ShouldBe([ErrorCodes.MoneyAmountNotPositive, BudgetErrorCodes.BorrowSameBucket, BudgetErrorCodes.EntryDateInFuture]);
        result.Errors.Cast<AppError>().Single(error => error.Code == BudgetErrorCodes.BorrowSameBucket).Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public void Repayment_HoldsTheValues()
    {
        var repayment = Repayment.Create(EntryId, Owner, Loan, Money.From(20m, Currency.Pln), Holiday, Today, Today, Info, FakeClock.DefaultStart).Value;

        repayment.Id.ShouldBe(EntryId.Value);
        repayment.LoanId.ShouldBe(Loan);
        repayment.Amount.ShouldBe(Money.From(20m, Currency.Pln));
        repayment.FromBucketId.ShouldBe(Holiday);
        repayment.OccurredOn.ShouldBe(Today);
        repayment.Note.ShouldBe("Car repair");
        repayment.SchemaVersion.ShouldBe(Repayment.CurrentSchemaVersion);
    }

    [Fact]
    public void Repayment_FromOutside_HasNoSource() =>
        Repayment.Create(EntryId, Owner, Loan, Money.From(1m, Currency.Pln), null, Today, Today, EntryInfo.None, FakeClock.DefaultStart).Value
            .FromBucketId.ShouldBeNull();

    [Fact]
    public void Repayment_AmountNotAboveZero_AndAFutureDate_Fail() =>
        Repayment.Create(EntryId, Owner, Loan, Money.From(-1m, Currency.Pln), null, Today.AddDays(1), Today, EntryInfo.None, FakeClock.DefaultStart)
            .Errors.Cast<AppError>().Select(error => error.Code).ShouldBe([ErrorCodes.MoneyAmountNotPositive, BudgetErrorCodes.EntryDateInFuture]);

    [Fact]
    public void Borrow_ContentIncludesEveryField()
    {
        var borrow = NewBorrow(Cushion, 10m, Holiday);

        borrow.HasSameContent(NewBorrow(Cushion, 10m, Holiday)).ShouldBeTrue();
        borrow.HasSameContent(NewBorrow(Holiday, 10m, Cushion)).ShouldBeFalse();
        borrow.HasSameContent(NewBorrow(Cushion, 11m, Holiday)).ShouldBeFalse();
        borrow.HasSameContent(NewBorrow(Cushion, 10m, null)).ShouldBeFalse();
        NewBorrow(Cushion, 10m, null).HasSameContent(borrow).ShouldBeFalse();
    }

    [Fact]
    public void Repayment_ContentIncludesEveryField()
    {
        var repayment = NewRepayment(Loan, 10m, Holiday);

        repayment.HasSameContent(NewRepayment(Loan, 10m, Holiday)).ShouldBeTrue();
        repayment.HasSameContent(NewRepayment(LoanId.From(Guid.CreateVersion7()), 10m, Holiday)).ShouldBeFalse();
        repayment.HasSameContent(NewRepayment(Loan, 11m, Holiday)).ShouldBeFalse();
        repayment.HasSameContent(NewRepayment(Loan, 10m, null)).ShouldBeFalse();
        NewRepayment(Loan, 10m, null).HasSameContent(repayment).ShouldBeFalse();
    }

    [Fact]
    public void ABorrow_NeverHasTheContentOfATransferOrARepayment()
    {
        var amount = Money.From(10m, Currency.Pln);
        var borrow = NewBorrow(Cushion, 10m, Holiday);
        var transfer = Transfer.Create(EntryId, Owner, Cushion, Holiday, amount, amount, null, Today, Today, EntryInfo.None, FakeClock.DefaultStart).Value;

        borrow.HasSameContent(transfer).ShouldBeFalse();
        transfer.HasSameContent(borrow).ShouldBeFalse();
        borrow.HasSameContent(NewRepayment(Loan, 10m, Holiday)).ShouldBeFalse();
    }

    private static Borrow NewBorrow(BucketId lender, decimal principal, BucketId? to) =>
        Borrow.Create(EntryId, Owner, lender, Money.From(principal, Currency.Pln), to, Today, Today, EntryInfo.None, FakeClock.DefaultStart).Value;

    private static Repayment NewRepayment(LoanId loan, decimal amount, BucketId? from) =>
        Repayment.Create(EntryId, Owner, loan, Money.From(amount, Currency.Pln), from, Today, Today, EntryInfo.None, FakeClock.DefaultStart).Value;
}
