using System.Globalization;

namespace FinTrack.Web.Formatting;

/// <summary>
/// The syntax of a number typed in the owner's locale, shared by <see cref="MoneyInput"/> and <see cref="RateInput"/>:
/// one optional leading minus (<c>-</c> or U+2212), ASCII digits, the locale's currency decimal separator at most
/// once with digits on both sides, and group separators only between digits and exactly where the locale's grouping
/// puts them (any of the usual spaces counts when the locale groups with a space, because a keyboard types a plain
/// one). Only the syntax: what the digits may mean (decimals of a currency, a positive rate) is the caller's rule.
/// </summary>
public static class LocaleNumber
{
    private static readonly string[] SpaceSeparators = [" ", "\u00A0", "\u202F"];

    /// <summary>
    /// Splits <paramref name="trimmed"/> (already without surrounding white space) into its sign, the digits before
    /// the decimal separator and the digits after it (empty when there is no separator); <c>false</c> for anything
    /// that is not a number in <paramref name="format"/>.
    /// </summary>
    public static bool TryRead(string trimmed, NumberFormatInfo format, out bool negative, out string integer, out string fraction)
    {
        ArgumentNullException.ThrowIfNull(trimmed);
        ArgumentNullException.ThrowIfNull(format);

        negative = trimmed.Length > 0 && trimmed[0] is '-' or MoneyFormatter.MinusSign;
        integer = string.Empty;
        fraction = string.Empty;

        var unsigned = negative ? trimmed[1..] : trimmed;
        var parts = unsigned.Split(format.CurrencyDecimalSeparator);
        if (parts.Length > 2 || !TryReadInteger(parts[0], format, out integer))
        {
            return false;
        }

        fraction = parts.Length == 2 ? parts[1] : string.Empty;
        return !(parts.Length == 2 && fraction.Length == 0) && fraction.All(char.IsAsciiDigit);
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
}
