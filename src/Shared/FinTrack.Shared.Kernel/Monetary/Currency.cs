using System.Collections.Frozen;
using FluentResults;

namespace FinTrack.Shared.Kernel.Monetary;

/// <summary>
/// A currency and its precision: the number of decimal places an amount may have.
/// </summary>
/// <remarks>
/// The set of currencies is closed: every currency is registered here, so a code can never
/// exist with two different precisions. To support a new currency, add a field and register it.
/// </remarks>
public sealed record Currency
{
    /// <summary>The largest precision a currency may have (crypto quantities, ARCHITECTURE section 3).</summary>
    public const int MaxDecimalPlaces = 18;

    /// <summary>Error code for a currency code that is not registered.</summary>
    public const string UnknownCurrencyCode = "currency.unknown";

    private const int MinCodeLength = 2;
    private const int MaxCodeLength = 10;

    /// <summary>Polish zloty.</summary>
    public static readonly Currency Pln = new("PLN", 2);

    /// <summary>Euro.</summary>
    public static readonly Currency Eur = new("EUR", 2);

    /// <summary>US dollar.</summary>
    public static readonly Currency Usd = new("USD", 2);

    private static readonly FrozenDictionary<string, Currency> Registered =
        new[] { Pln, Eur, Usd }.ToFrozenDictionary(currency => currency.Code, StringComparer.Ordinal);

    private Currency(string code, int decimalPlaces)
    {
        if (!IsValidCode(code))
        {
            throw new ArgumentException($"A currency code has {MinCodeLength} to {MaxCodeLength} upper-case letters or digits.", nameof(code));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(decimalPlaces);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(decimalPlaces, MaxDecimalPlaces);

        Code = code;
        DecimalPlaces = decimalPlaces;
    }

    /// <summary>The currency code, for example <c>PLN</c>.</summary>
    public string Code { get; }

    /// <summary>How many decimal places an amount in this currency may have (0 to 18).</summary>
    public int DecimalPlaces { get; }

    /// <summary>
    /// Finds a registered currency by its exact code (upper case, no spaces).
    /// Fails with <see cref="UnknownCurrencyCode"/> for anything else.
    /// </summary>
    public static Result<Currency> FromCode(string? code) =>
        code is not null && Registered.TryGetValue(code, out var currency)
            ? Result.Ok(currency)
            : Result.Fail<Currency>(new Error(UnknownCurrencyCode));

    /// <inheritdoc />
    public override string ToString() => Code;

    /// <summary>Creates an unregistered currency. For tests only, to cover every precision.</summary>
    internal static Currency Define(string code, int decimalPlaces) => new(code, decimalPlaces);

    private static bool IsValidCode(string? code) =>
        code is { Length: >= MinCodeLength and <= MaxCodeLength }
        && code.All(character => char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character));
}
