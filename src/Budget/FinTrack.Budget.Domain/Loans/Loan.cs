using FinTrack.Budget.Domain.Buckets;
using FinTrack.Shared.Kernel.Monetary;

namespace FinTrack.Budget.Domain.Loans;

/// <summary>
/// A loan from a savings bucket to the owner (ARCHITECTURE 4.4, plan 1b B2), derived by the state builder from its
/// <c>Borrow</c> and the effective <c>Repayment</c>s up to the point of the replay. Never stored. Every amount is in the
/// lender's currency.
/// </summary>
/// <param name="LoanId">The loan: its borrow's entry ID.</param>
/// <param name="LenderBucketId">The savings bucket the money was borrowed from.</param>
/// <param name="ToBucketId">The bucket the money went to, or <c>null</c> when it left the app.</param>
/// <param name="Principal">What was borrowed, above zero.</param>
/// <param name="BorrowedOn">The borrow's business date.</param>
/// <param name="Repaid">The sum of the effective repayments so far.</param>
/// <param name="ClosedOn">The date of the repayment that brought <see cref="Outstanding"/> to zero, or <c>null</c> while open.</param>
public sealed record Loan(
    LoanId LoanId,
    BucketId LenderBucketId,
    BucketId? ToBucketId,
    Money Principal,
    DateOnly BorrowedOn,
    Money Repaid,
    DateOnly? ClosedOn)
{
    /// <summary><see cref="Principal"/> − <see cref="Repaid"/>; never below zero in an accepted journal (invariant 4).</summary>
    public Money Outstanding => Principal - Repaid;

    /// <summary><see cref="LoanStatus.Closed"/> once nothing is outstanding, otherwise <see cref="LoanStatus.Open"/>.</summary>
    public LoanStatus Status => Outstanding.Amount > 0m ? LoanStatus.Open : LoanStatus.Closed;
}
