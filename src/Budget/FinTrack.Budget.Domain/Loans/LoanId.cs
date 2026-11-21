using FinTrack.Shared.Kernel.Ids;

namespace FinTrack.Budget.Domain.Loans;

/// <summary>
/// Identifies a loan (ARCHITECTURE 4.4, plan 1b B2): the ID of the <c>Borrow</c> entry that opened it. A loan is never
/// stored on its own; it is derived by replaying its borrow and repayments.
/// </summary>
public readonly record struct LoanId : IStronglyTypedId<LoanId>
{
    private LoanId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static LoanId From(Guid value) =>
        value == Guid.Empty ? throw new ArgumentException("A loan identifier cannot be empty.", nameof(value)) : new LoanId(value);

    /// <summary>The identifier in the standard <c>D</c> format.</summary>
    public override string ToString() => Value.ToString();
}
