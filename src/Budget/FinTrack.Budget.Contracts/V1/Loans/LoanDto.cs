using FinTrack.Shared.Kernel.Monetary;

namespace FinTrack.Budget.Contracts.V1.Loans;

/// <summary>
/// One loan (ARCHITECTURE 4.4, plan 1b B19), version 1 of the contract: money borrowed from a savings bucket of the owner
/// with the obligation to return it. Derived from its <c>Borrow</c> and the effective <c>Repayment</c>s; every amount is in
/// the lender's currency.
/// </summary>
/// <param name="Id">The loan's ID: the ID of the <c>Borrow</c> entry that opened it (the journal's <c>loanId</c>).</param>
/// <param name="LenderBucketId">The savings bucket the money was borrowed from.</param>
/// <param name="ToBucketId">The bucket the money went to, or <c>null</c> when it left the app.</param>
/// <param name="Principal">What was borrowed, above zero.</param>
/// <param name="BorrowedOn">The borrow's business date.</param>
/// <param name="Repaid">The sum of the repayments that count (reversed ones do not); never above <paramref name="Principal"/>.</param>
/// <param name="Outstanding"><paramref name="Principal"/> − <paramref name="Repaid"/>: what is still owed to the lender; never below zero.</param>
/// <param name="Status"><c>Open</c> while something is outstanding, <c>Closed</c> once nothing is.</param>
/// <param name="ClosedOn">The business date of the repayment that closed the loan; <c>null</c> while it is open.</param>
/// <param name="Note">The borrow's note, or <c>null</c>.</param>
public sealed record LoanDto(
    Guid Id,
    Guid LenderBucketId,
    Guid? ToBucketId,
    Money Principal,
    DateOnly BorrowedOn,
    Money Repaid,
    Money Outstanding,
    LoanStatus Status,
    DateOnly? ClosedOn,
    string? Note);

/// <summary>Whether a loan still has principal outstanding. JSON: the member's name. A copy of the domain's enum with the same member names.</summary>
public enum LoanStatus
{
    /// <summary>Principal is outstanding.</summary>
    Open,

    /// <summary>Fully repaid; stays in history. A reversed repayment reopens it.</summary>
    Closed,
}
