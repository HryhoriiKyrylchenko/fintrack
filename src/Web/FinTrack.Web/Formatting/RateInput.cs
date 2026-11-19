using System.Globalization;
using FinTrack.Shared.Kernel.Results;
using FluentResults;

namespace FinTrack.Web.Formatting;

/// <summary>
/// Reads an exchange rate typed by the owner (a transfer between currencies, plan 1a D6): a number above zero in
/// the owner's locale, with the same syntax as an amount (<see cref="LocaleNumber"/>), never rounded. The rate only
/// computes the amount received; the amounts are what is stored.
/// </summary>
public static class RateInput
{
    /// <summary>Nothing was typed.</summary>
    public const string Required = "input.rate_required";

    /// <summary>
    /// The text is not a rate above zero in the owner's locale. Parameter <c>example</c>: 4.2735 written in that
    /// locale, never the typed text.
    /// </summary>
    public const string Invalid = "input.rate_invalid";

    private const decimal ExampleRate = 4.2735m;
    private const int MaxDecimals = 28;

    /// <summary>The rate typed as <paramref name="text"/>, read in <paramref name="culture"/>.</summary>
    public static Result<decimal> Parse(string? text, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return Result.Fail<decimal>(AppError.Validation(Required));
        }

        if (!LocaleNumber.TryRead(trimmed, culture.NumberFormat, out var negative, out var integer, out var fraction)
            || negative
            || fraction.TrimEnd('0').Length > MaxDecimals)
        {
            return InvalidIn(culture);
        }

        var significant = fraction.TrimEnd('0');
        var invariant = significant.Length == 0 ? integer : $"{integer}.{significant}";
        return decimal.TryParse(invariant, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate) && rate > 0m
            ? Result.Ok(rate)
            : InvalidIn(culture);
    }

    /// <summary>
    /// A rate for reading, in <paramref name="culture"/>: at most six decimals, trailing zeros left out. Display only;
    /// the rate itself is derived from the amounts, which are what is stored (plan 1a D6).
    /// </summary>
    public static string Format(decimal rate, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return rate.ToString("#,##0.######", MoneyFormatter.NumberFormat(culture));
    }

    private static Result<decimal> InvalidIn(CultureInfo culture) =>
        Result.Fail<decimal>(AppError.Validation(Invalid, ("example", ExampleRate.ToString("N4", MoneyFormatter.NumberFormat(culture)))));
}
