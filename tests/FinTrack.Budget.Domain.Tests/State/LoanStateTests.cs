using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.Loans;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Domain.Tests.State;

/// <summary>
/// Loans in the state builder (ARCHITECTURE 4.3, 4.4, plan 1b B2, B3): the lender's <c>OutstandingPrincipal</c>, the
/// destination and source like transfers, the loan derived by replay (open, closed, reopened by a reversal), and
/// invariant 4 at every point (<see cref="BudgetState.FirstOverRepayment"/>).
/// </summary>
public class LoanStateTests
{
    private static readonly OwnerId Owner = OwnerId.From(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly DateOnly Today = new(2026, 1, 15);

    private readonly Bucket cushion = Bucket.Create(BucketId.From(Guid.CreateVersion7()), Owner, "Cushion", Currency.Pln, BucketKind.Savings, null, []).Value;
    private readonly Bucket holiday = Bucket.Create(BucketId.From(Guid.CreateVersion7()), Owner, "Holiday", Currency.Pln, BucketKind.Savings, null, []).Value;
    private int recorded;

    [Fact]
    public void ABorrowIntoABucket_LowersTheLendersAvailable_RaisesTheDestination_AndLeavesTheTotal()
    {
        // ARCHITECTURE 4.3's example, scaled: 1,000 contributed, 100 borrowed and not yet returned → 900 available.
        var state = Replay(Deposit(cushion, 1000m, -10), Borrow(cushion, 100m, holiday, -5));

        var lender = state.BalanceOf(cushion.BucketId);
        lender.NetContributed.ShouldBe(Pln(1000m));
        lender.OutstandingPrincipal.ShouldBe(Pln(100m));
        lender.Available.ShouldBe(Pln(900m));
        var destination = state.BalanceOf(holiday.BucketId);
        destination.NetContributed.ShouldBe(Pln(100m));
        destination.Available.ShouldBe(Pln(100m));
        destination.Deposited.ShouldBe(Money.Zero(Currency.Pln));
        (lender.Available + destination.Available).ShouldBe(Pln(1000m));
    }

    [Fact]
    public void ABorrowToOutside_LowersTheTotal()
    {
        var state = Replay(Deposit(cushion, 1000m, -10), Borrow(cushion, 100m, null, -5));

        state.BalanceOf(cushion.BucketId).Available.ShouldBe(Pln(900m));
        state.BalanceOf(holiday.BucketId).Available.ShouldBe(Money.Zero(Currency.Pln));
    }

    [Fact]
    public void ALoan_IsDerived_OpenWithItsPrincipal()
    {
        var borrow = Borrow(cushion, 100m, holiday, -5);

        var loan = Replay(Deposit(cushion, 1000m, -10), borrow).Loans.ShouldHaveSingleItem();

        loan.ShouldBe(new Loan(borrow.LoanId, cushion.BucketId, holiday.BucketId, Pln(100m), Today.AddDays(-5), Money.Zero(Currency.Pln), ClosedOn: null));
        loan.Outstanding.ShouldBe(Pln(100m));
        loan.Status.ShouldBe(LoanStatus.Open);
    }

    [Fact]
    public void RepaymentsInParts_LowerTheOutstandingPrincipal_AndCloseTheLoanAtZero()
    {
        var borrow = Borrow(cushion, 100m, holiday, -9);
        var state = Replay(Deposit(cushion, 1000m, -10), borrow, Repay(borrow, 30m, holiday, -6));

        var loan = state.FindLoan(borrow.LoanId)!;
        loan.Repaid.ShouldBe(Pln(30m));
        loan.Outstanding.ShouldBe(Pln(70m));
        loan.Status.ShouldBe(LoanStatus.Open);
        loan.ClosedOn.ShouldBeNull();
        state.BalanceOf(cushion.BucketId).OutstandingPrincipal.ShouldBe(Pln(70m));
        state.BalanceOf(holiday.BucketId).NetContributed.ShouldBe(Pln(70m));

        var closed = Replay(Deposit(cushion, 1000m, -10), borrow, Repay(borrow, 30m, holiday, -6), Repay(borrow, 70m, null, -2));

        var paid = closed.FindLoan(borrow.LoanId)!;
        paid.Outstanding.ShouldBe(Money.Zero(Currency.Pln));
        paid.Status.ShouldBe(LoanStatus.Closed);
        paid.ClosedOn.ShouldBe(Today.AddDays(-2));
        closed.BalanceOf(cushion.BucketId).Available.ShouldBe(Pln(1000m));
        closed.FirstOverRepayment.ShouldBeNull();
    }

    [Fact]
    public void RepaymentsAreNeverIncome_AndARepaymentFromOutsideRaisesTheTotal()
    {
        var borrow = Borrow(cushion, 100m, null, -9);
        var state = Replay(Deposit(cushion, 1000m, -10), borrow, Repay(borrow, 40m, null, -6));

        state.BalanceOf(cushion.BucketId).Available.ShouldBe(Pln(940m));
        state.BalanceOf(cushion.BucketId).NetContributed.ShouldBe(Pln(1000m));
        state.BalanceOf(cushion.BucketId).Deposited.ShouldBe(Pln(1000m));
    }

    [Fact]
    public void AReversedRepayment_ReopensTheLoan()
    {
        var borrow = Borrow(cushion, 100m, holiday, -9);
        var last = Repay(borrow, 100m, holiday, -2);

        var state = Replay(Deposit(cushion, 1000m, -10), borrow, last, Reverse(last));

        var loan = state.FindLoan(borrow.LoanId)!;
        loan.Status.ShouldBe(LoanStatus.Open);
        loan.ClosedOn.ShouldBeNull();
        loan.Outstanding.ShouldBe(Pln(100m));
    }

    [Fact]
    public void AReversedBorrow_HasNoLoan()
    {
        var borrow = Borrow(cushion, 100m, holiday, -9);

        var state = Replay(Deposit(cushion, 1000m, -10), borrow, Reverse(borrow));

        state.Loans.ShouldBeEmpty();
        state.FindLoan(borrow.LoanId).ShouldBeNull();
        state.BalanceOf(cushion.BucketId).OutstandingPrincipal.ShouldBe(Money.Zero(Currency.Pln));
    }

    [Fact]
    public void ARepaymentAboveTheOutstandingPrincipal_BreaksInvariant4_OnItsDay()
    {
        var borrow = Borrow(cushion, 100m, holiday, -9);

        var state = Replay(Deposit(cushion, 1000m, -10), borrow, Repay(borrow, 60m, holiday, -6), Repay(borrow, 40.01m, null, -3));

        state.FirstOverRepayment.ShouldBe(new OverRepayment(borrow.LoanId, Today.AddDays(-3)));
    }

    [Fact]
    public void ARepaymentBeforeItsBorrow_BreaksInvariant4_OnItsDay_AndOnlyItsSourcePays()
    {
        var borrow = Borrow(cushion, 100m, holiday, -5);

        var state = Replay(Deposit(cushion, 1000m, -10), borrow, Repay(borrow, 10m, null, -7));

        state.FirstOverRepayment.ShouldBe(new OverRepayment(borrow.LoanId, Today.AddDays(-7)));
        state.FindLoan(borrow.LoanId)!.Repaid.ShouldBe(Money.Zero(Currency.Pln));
    }

    [Fact]
    public void OnlyTheFirstOverRepaymentIsKept()
    {
        var first = Borrow(cushion, 10m, null, -9);
        var second = Borrow(cushion, 10m, null, -8);

        var state = Replay(Deposit(cushion, 1000m, -10), first, second, Repay(second, 11m, null, -5), Repay(first, 11m, null, -4));

        state.FirstOverRepayment.ShouldBe(new OverRepayment(second.LoanId, Today.AddDays(-5)));
    }

    [Fact]
    public void ABorrowAboveTheLendersAvailable_IsAnOverdraftOnItsDay()
    {
        var state = Replay(Deposit(cushion, 100m, -10), Borrow(cushion, 100.01m, holiday, -4));

        state.FirstOverdraft.ShouldBe(new Overdraft(cushion.BucketId, Today.AddDays(-4)));
    }

    [Fact]
    public void ARepaymentFromABucket_IsHeldToItsAvailable()
    {
        var borrow = Borrow(cushion, 100m, holiday, -9);

        var state = Replay(Deposit(cushion, 1000m, -10), borrow, Spend(holiday, 80m, -8), Repay(borrow, 30m, holiday, -6));

        state.FirstOverdraft.ShouldBe(new Overdraft(holiday.BucketId, Today.AddDays(-6)));
    }

    [Fact]
    public void LoansAreListedInTheOrderOfTheirBorrows()
    {
        var later = Borrow(cushion, 1m, null, -3);
        var earlier = Borrow(holiday, 1m, null, -6);

        var state = Replay(Deposit(cushion, 10m, -10), Deposit(holiday, 10m, -10), later, earlier);

        state.Loans.Select(loan => loan.LoanId).ShouldBe([earlier.LoanId, later.LoanId]);
    }

    private BudgetState Replay(params JournalEntry[] journal) => BudgetState.Replay([cushion, holiday], journal, Today);

    private Borrow Borrow(Bucket lender, decimal principal, Bucket? to, int day) =>
        Domain.Journal.Borrow.Create(NewId(), Owner, lender.BucketId, Pln(principal), to?.BucketId, Today.AddDays(day), Today, EntryInfo.None, NextRecordedAt()).Value;

    private Repayment Repay(Borrow borrow, decimal amount, Bucket? from, int day) =>
        Repayment.Create(NewId(), Owner, borrow.LoanId, Pln(amount), from?.BucketId, Today.AddDays(day), Today, EntryInfo.None, NextRecordedAt()).Value;

    private Deposit Deposit(Bucket bucket, decimal amount, int day) =>
        Domain.Journal.Deposit.Create(NewId(), Owner, bucket.BucketId, Pln(amount), Today.AddDays(day), Today, EntryInfo.None, NextRecordedAt()).Value;

    private Spend Spend(Bucket bucket, decimal amount, int day) =>
        Domain.Journal.Spend.Create(NewId(), Owner, bucket.BucketId, Pln(amount), Today.AddDays(day), Today, EntryInfo.None, NextRecordedAt()).Value;

    private Reversal Reverse(JournalEntry entry) =>
        Reversal.Create(NewId(), Owner, entry.EntryId, Today, Today, EntryInfo.None, NextRecordedAt()).Value;

    private DateTimeOffset NextRecordedAt() => FakeClock.DefaultStart.AddSeconds(recorded++);

    private static JournalEntryId NewId() => JournalEntryId.From(Guid.CreateVersion7());

    private static Money Pln(decimal amount) => Money.From(amount, Currency.Pln);
}
