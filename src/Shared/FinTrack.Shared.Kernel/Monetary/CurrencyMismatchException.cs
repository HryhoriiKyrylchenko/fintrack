namespace FinTrack.Shared.Kernel.Monetary;

/// <summary>
/// Thrown when amounts in two different currencies are added, subtracted or compared.
/// This is a programming error, not a business rule failure: amounts in different
/// currencies are combined only after an explicit <see cref="Money.Convert"/> (invariant 11).
/// </summary>
public sealed class CurrencyMismatchException : InvalidOperationException
{
    /// <summary>Creates the exception for the two currencies involved.</summary>
    public CurrencyMismatchException(Currency left, Currency right)
        : base($"Amounts in {left?.Code} and {right?.Code} cannot be combined without an explicit conversion.")
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        Left = left;
        Right = right;
    }

    /// <summary>The currency of the left operand.</summary>
    public Currency Left { get; }

    /// <summary>The currency of the right operand.</summary>
    public Currency Right { get; }
}
