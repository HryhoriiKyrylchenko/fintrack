using System.Globalization;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FluentResults;

namespace FinTrack.Web.Formatting;

/// <summary>
/// Reads an amount typed by the owner, in the owner's locale (DESIGN 6). Strict, because a misread amount is
/// worse than a rejected one: <c>12.50</c> typed in <c>de-DE</c>, where the dot groups thousands, is rejected,
/// never read as 1250 or as 12.5. Never rounds: too many decimals is the kernel's
/// <see cref="ErrorCodes.MoneyTooManyDecimals"/>, the same check the Api runs.
/// </summary>
/// <remarks>
/// Accepted: surrounding white space; one optional leading minus (<c>-</c> or <c>−</c>; whether a negative amount is
/// allowed is the form's rule); ASCII digits; the locale's decimal separator at most once, with digits on both
/// sides; group separators only between digits and exactly where the locale's grouping puts them (any of the
/// usual spaces counts when the locale groups with a space, because a keyboard types a plain one). The result
/// has the currency's scale (<c>12.5</c> becomes <c>12.50</c>; trailing zeros are not decimals, as in the kernel),
/// so the Api receives one spelling.
/// </remarks>
public static class MoneyInput
{
    /// <summary>Nothing was typed.</summary>
    public const string Required = "input.amount_required";

    /// <summary>
    /// The text is not an amount in the owner's locale. Parameter <c>example</c>: 1234.56 written in that locale
    /// with the currency's precision, never the text that was typed.
    /// </summary>
    public const string Invalid = "input.amount_invalid";

    private const decimal ExampleAmount = 1234.56m;
    private const int MaxDecimalScale = 28;
    private static readonly string[] SpaceSeparators = [" ", "\u00A0", "\u202F"];

    /// <summary>The amount typed as <paramref name="text"/> in <paramref name="currency"/>, read in <paramref name="culture"/>.</summary>
    public static Result<Money> Parse(string? text, Currency currency, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(currency);
        ArgumentNullException.ThrowIfNull(culture);

        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return Result.Fail<Money>(AppError.Validation(Required));
        }

        var negative = trimmed[0] is '-' or MoneyFormatter.MinusSign;
        var unsigned = negative ? trimmed[1..] : trimmed;

        var format = culture.NumberFormat;
        var parts = unsigned.Split(format.CurrencyDecimalSeparator);
        if (parts.Length > 2 || !TryReadInteger(parts[0], format, out var integer))
        {
            return InvalidIn(currency, culture);
        }

        var fraction = parts.Length == 2 ? parts[1] : string.Empty;
        if ((parts.Length == 2 && fraction.Length == 0) || !fraction.All(char.IsAsciiDigit))
        {
            return InvalidIn(currency, culture);
        }

        // Trailing zeros are not decimals. More decimals than the currency allows fail in the kernel; with more
        // than a decimal holds, the smallest value of that scale stands in, so nothing is ever rounded to fit.
        var significant = fraction.TrimEnd('0');
        if (significant.Length > currency.DecimalPlaces)
        {
            var scale = (byte)Math.Min(significant.Length, MaxDecimalScale);
            return Money.Create(new decimal(1, 0, 0, false, scale), currency);
        }

        var invariant = significant.Length == 0 ? integer : $"{integer}.{significant}";
        if (!decimal.TryParse(invariant, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            return InvalidIn(currency, culture);
        }

        var amount = Money.Create(negative ? -value : value, currency);
        return amount.IsFailed ? amount : Result.Ok(Money.From(WithScale(amount.Value.Amount, currency.DecimalPlaces), currency));
    }

    // The integer part: digits, grouped exactly as the locale groups them, or not grouped at all.
    private static bool TryReadInteger(string text, NumberFormatInfo format, out string digits)
    {
        digits = string.Empty;
        var separator = format.CurrencyGroupSeparator;
        var separators = SpaceSeparators.Contains(separator) ? SpaceSeparators : [separator];
        var groups = text.Split(separators, StringSplitOptions.None);

        if (groups.Any(group => group.Length == 0 || !group.All(char.IsAsciiDigit)))
        {
            return false;
        }

        if (groups.Length > 1 && !FitsGrouping(groups, format.CurrencyGroupSizes))
        {
            return false;
        }

        digits = string.Concat(groups);
        return true;
    }

    // Group sizes are counted from the decimal separator leftwards; the last size repeats (en-IN: 3, then 2s).
    // Every group but the leftmost has exactly its size; the leftmost has 1 up to its size.
    private static bool FitsGrouping(string[] groups, int[] sizes)
    {
        for (var index = 0; index < groups.Length; index++)
        {
            var fromRight = groups.Length - 1 - index;
            var size = sizes.Length == 0 ? 3 : sizes[Math.Min(fromRight, sizes.Length - 1)];
            if (size <= 0)
            {
                return false;
            }

            var leftmost = index == 0;
            if (leftmost ? groups[index].Length > size : groups[index].Length != size)
            {
                return false;
            }
        }

        return true;
    }

    // The same value with exactly `places` decimals: only trailing zeros are added or removed.
    private static decimal WithScale(decimal value, int places) =>
        decimal.Round(value, places) + new decimal(0, 0, 0, false, (byte)places);

    private static Result<Money> InvalidIn(Currency currency, CultureInfo culture)
    {
        var example = ExampleAmount.ToString("N" + currency.DecimalPlaces.ToString(CultureInfo.InvariantCulture), MoneyFormatter.NumberFormat(culture));
        return Result.Fail<Money>(AppError.Validation(Invalid, ("example", example)));
    }
}
