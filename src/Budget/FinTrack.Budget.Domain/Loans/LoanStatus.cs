namespace FinTrack.Budget.Domain.Loans;

/// <summary>Whether a loan still has principal outstanding (ARCHITECTURE 4.4).</summary>
public enum LoanStatus
{
    /// <summary>Principal is outstanding.</summary>
    Open,

    /// <summary>Fully repaid; stays in history. A reversed repayment reopens it.</summary>
    Closed,
}
