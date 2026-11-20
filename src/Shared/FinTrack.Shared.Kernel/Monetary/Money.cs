using System.Globalization;
using FinTrack.Shared.Kernel.Results;
using FluentResults;

namespace FinTrack.Shared.Kernel.Monetary;

/// <summary>
/// An amount in one currency. Immutable. The amount never has more decimal places than the
/// currency allows. Amounts may be negative; rules about signs belong to the domain.
/// </summary>
/// <remarks>
/// <para>
/// Adding, subtracting or comparing amounts in different currencies throws
/// <see cref="CurrencyMismatchException"/>; equality between different currencies is simply false.
/// </para>
/// <para>
/// Messages of exceptions thrown here never contain amounts, because exceptions are logged
/// and amounts must never be logged (CLAUDE.md, Logging).
/// </para>
/// </remarks>
public sealed record Money
{
    // Every value passes here, so every amount has its currency's scale (see AtScale).
    private Money(decimal amount, Currency currency)
    {
        Amount = AtScale(amount, currency.DecimalPlaces);
        Currency = currency;
    }

    /// <summary>
    /// The amount, with exactly <see cref="Currency.DecimalPlaces"/> decimal places: <c>50</c> PLN is
    /// <c>50.00</c>, so every amount has one spelling in JSON and in storage (ARCHITECTURE 2.7). Zero has no sign.
    /// </summary>
    public decimal Amount { get; }

    /// <summary>The currency of the amount.</summary>
    public Currency Currency { get; }

    /// <summary>
    /// Creates an amount from an untrusted value, for example user input.
    /// Fails with <see cref="ErrorCodes.MoneyTooManyDecimals"/> when the value has more decimal
    /// places than the currency allows; it is never rounded silently. Trailing zeros do not count.
    /// The amount itself is not an error parameter, because parameters may end up in logs.
    /// </summary>
    public static Result<Money> Create(decimal amount, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);

        return FitsPrecision(amount, currency)
            ? Result.Ok(new Money(amount, currency))
            : Result.Fail<Money>(AppError.Validation(
                ErrorCodes.MoneyTooManyDecimals,
                ("currency", currency.Code),
                ("maxDecimalPlaces", currency.DecimalPlaces)));
    }

    /// <summary>
    /// Creates an amount from a trusted value, for example one read from the database.
    /// Throws <see cref="ArgumentException"/> when the value does not fit the currency, which is a bug.
    /// </summary>
    public static Money From(decimal amount, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);

        if (!FitsPrecision(amount, currency))
        {
            throw new ArgumentException($"The amount has more than {currency.DecimalPlaces} decimal places allowed for {currency.Code}.", nameof(amount));
        }

        return new Money(amount, currency);
    }

    /// <summary>Zero in <paramref name="currency"/>.</summary>
    public static Money Zero(Currency currency) => From(0m, currency);

    /// <summary>
    /// Rounds a raw value to the precision of <paramref name="currency"/> with banker's rounding
    /// (a midpoint goes to the even digit: 2.345 becomes 2.34, 2.355 becomes 2.36).
    /// </summary>
    public static Money Round(decimal amount, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);

        return new Money(decimal.Round(amount, currency.DecimalPlaces, MidpointRounding.ToEven), currency);
    }

    /// <summary>
    /// Converts to <paramref name="target"/> at an explicit <paramref name="rate"/>
    /// (units of the target per unit of this currency), rounded with banker's rounding.
    /// </summary>
    public Money Convert(decimal rate, Currency target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate);

        return Round(Amount * rate, target);
    }

    /// <summary>
    /// Splits the amount into parts proportional to <paramref name="weights"/>. The parts always
    /// sum exactly to this amount and never have the opposite sign.
    /// </summary>
    /// <remarks>
    /// Each part is cut toward zero at the currency's precision; what is left over (less than one
    /// minor unit per part) is added to the first part (ARCHITECTURE section 3).
    /// </remarks>
    /// <param name="weights">One positive weight per part, in the order of the parts.</param>
    public IReadOnlyList<Money> Split(IReadOnlyList<decimal> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);

        if (weights.Count == 0)
        {
            throw new ArgumentException("At least one weight is required.", nameof(weights));
        }

        if (weights.Any(weight => weight <= 0m))
        {
            throw new ArgumentException("Every weight must be positive.", nameof(weights));
        }

        var totalWeight = weights.Sum();
        var parts = weights
            .Select(weight => decimal.Round(Amount * weight / totalWeight, Currency.DecimalPlaces, MidpointRounding.ToZero))
            .ToArray();

        parts[0] += Amount - parts.Sum();

        return [.. parts.Select(part => new Money(part, Currency))];
    }

    /// <summary>Adds two amounts in the same currency.</summary>
    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    /// <summary>Subtracts two amounts in the same currency.</summary>
    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    /// <summary>The same amount with the opposite sign.</summary>
    public static Money operator -(Money value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Money(-value.Amount, value.Currency);
    }

    /// <summary>Compares two amounts in the same currency.</summary>
    public static bool operator <(Money left, Money right) => Compare(left, right) < 0;

    /// <summary>Compares two amounts in the same currency.</summary>
    public static bool operator >(Money left, Money right) => Compare(left, right) > 0;

    /// <summary>Compares two amounts in the same currency.</summary>
    public static bool operator <=(Money left, Money right) => Compare(left, right) <= 0;

    /// <summary>Compares two amounts in the same currency.</summary>
    public static bool operator >=(Money left, Money right) => Compare(left, right) >= 0;

    /// <summary>The amount and the currency code, for example <c>-1234.5 PLN</c> (invariant culture, for tests and debugging; never log it).</summary>
    public override string ToString() => $"{Amount.ToString(CultureInfo.InvariantCulture)} {Currency.Code}";

    private static int Compare(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return left.Amount.CompareTo(right.Amount);
    }

    private static void EnsureSameCurrency(Money left, Money right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Currency != right.Currency)
        {
            throw new CurrencyMismatchException(left.Currency, right.Currency);
        }
    }

    // The same value with exactly `places` decimals: only trailing zeros are added or removed, never a digit
    // (every caller has checked the precision or rounded first; a value that does not fit is a bug). A decimal
    // holds 28 to 29 significant digits, so a value too large for its scale keeps fewer decimals.
    private static decimal AtScale(decimal amount, int places)
    {
        var zero = new decimal(0, 0, 0, false, (byte)places);
        if (amount == 0m)
        {
            return zero;
        }

        var trimmed = decimal.Round(amount, places);
        return trimmed == amount
            ? trimmed + zero
            : throw new InvalidOperationException("An amount with more decimal places than its currency allows reached Money (a bug).");
    }

    private static bool FitsPrecision(decimal amount, Currency currency) =>
        decimal.Round(amount, currency.DecimalPlaces) == amount;
}
