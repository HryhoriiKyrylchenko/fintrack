using System.Globalization;
using FinTrack.Shared.Kernel.Monetary;

namespace FinTrack.Web.Formatting;

/// <summary>Which sign an amount shows.</summary>
public enum MoneySign
{
    /// <summary>Only a negative amount has a sign (−): balances and totals.</summary>
    NegativeOnly,

    /// <summary>A positive amount shows + and a negative one −; zero shows none: movements and changes.</summary>
    Always,
}

/// <summary>
/// Turns an amount into text for the owner (DESIGN 6): always with the currency's ISO code, with exactly the
/// currency's number of decimals (<see cref="Currency.DecimalPlaces"/>), with the group and decimal separators
/// of the owner's locale, and with the real minus sign (U+2212). Pure: the culture is passed in, so components
/// pass <see cref="CultureInfo.CurrentCulture"/>, which is the owner's locale (<c>OwnerCulture</c>).
/// </summary>
/// <remarks>
/// <para>
/// The ISO code, not a symbol: "$" is shared by many currencies and a locale's own symbol ("zł") belongs to its
/// own currency, so a symbol could leave the currency unclear. The code goes where the locale puts its currency
/// symbol (before the number in <c>en-US</c>, after it in <c>pl-PL</c>), separated by a no-break space so it never
/// wraps away from the number.
/// </para>
/// <para>
/// The sign always comes first, whatever the locale's pattern for negative currency (some put it after the number
/// or in parentheses): one rule is easier to read in a column of mixed amounts. Amounts are never rounded here:
/// <see cref="Money"/> never has more decimals than its currency, so the text only pads to the precision.
/// </para>
/// </remarks>
public static class MoneyFormatter
{
    /// <summary>The minus sign shown (U+2212), wider than a hyphen and aligned with the plus sign.</summary>
    public const char MinusSign = '\u2212';

    private const char NoBreakSpace = '\u00A0';

    /// <summary>The amount as text in <paramref name="culture"/>.</summary>
    public static string Format(Money money, CultureInfo culture, MoneySign sign = MoneySign.NegativeOnly)
    {
        ArgumentNullException.ThrowIfNull(money);
        ArgumentNullException.ThrowIfNull(culture);

        var number = Math.Abs(money.Amount).ToString("N" + money.Currency.DecimalPlaces.ToString(CultureInfo.InvariantCulture), NumberFormat(culture));
        var prefix = money.Amount switch
        {
            < 0m => MinusSign.ToString(),
            > 0m when sign == MoneySign.Always => "+",
            _ => string.Empty,
        };

        return CodeGoesFirst(culture)
            ? $"{prefix}{money.Currency.Code}{NoBreakSpace}{number}"
            : $"{prefix}{number}{NoBreakSpace}{money.Currency.Code}";
    }

    /// <summary>The locale's number format for money: its currency separators and grouping.</summary>
    internal static NumberFormatInfo NumberFormat(CultureInfo culture)
    {
        var source = culture.NumberFormat;
        var format = (NumberFormatInfo)source.Clone();
        format.NumberDecimalSeparator = source.CurrencyDecimalSeparator;
        format.NumberGroupSeparator = source.CurrencyGroupSeparator;
        format.NumberGroupSizes = source.CurrencyGroupSizes;
        return format;
    }

    // CurrencyPositivePattern: 0 "$n", 1 "n$", 2 "$ n", 3 "n $".
    private static bool CodeGoesFirst(CultureInfo culture) => culture.NumberFormat.CurrencyPositivePattern is 0 or 2;
}
