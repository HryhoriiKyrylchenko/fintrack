using System.Globalization;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;
using FinTrack.Web.Formatting;
using FluentResults;

namespace FinTrack.Web.Tests.Formatting;

/// <summary>
/// Amounts typed by the owner, read in the owner's locale: only that locale's decimal separator, group
/// separators only where the locale puts them, and never rounded (DESIGN 6). What cannot be read exactly
/// is rejected with a code, never guessed.
/// </summary>
public class MoneyInputTests
{
    public static TheoryData<string, string, string, decimal> Accepted => new()
    {
        { "pl-PL", "PLN", "1234,56", 1234.56m },
        { "pl-PL", "PLN", "1 234,56", 1234.56m },
        { "pl-PL", "PLN", "1\u00A0234,56", 1234.56m },
        { "pl-PL", "EUR", "1\u202F234\u00A0567", 1234567m },
        { "pl-PL", "USD", "  0,5  ", 0.5m },
        { "en-US", "PLN", "1,234.56", 1234.56m },
        { "en-US", "EUR", "1234.5", 1234.5m },
        { "en-US", "USD", "-12.30", -12.3m },
        { "en-US", "USD", "\u221212.30", -12.3m },
        { "de-DE", "PLN", "1.234,56", 1234.56m },
        { "de-DE", "EUR", "12,5", 12.5m },
        { "de-DE", "USD", "1.000.000", 1000000m },
        { "fr-FR", "EUR", "1 234,56", 1234.56m },
        { "en-IN", "USD", "12,34,567.50", 1234567.5m },
        { "en-US", "PLN", "12.500", 12.5m },
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public void Accepted_InTheLocalesSpelling(string locale, string currency, string text, decimal expected)
    {
        var money = Parse(text, currency, locale).Value;

        money.Amount.ShouldBe(expected);
        money.Currency.Code.ShouldBe(currency);
    }

    [Theory]
    [InlineData("12.5", "12.50")]
    [InlineData("12", "12.00")]
    [InlineData("12.500", "12.50")]
    [InlineData("0", "0.00")]
    public void Amount_HasTheCurrencysScale(string text, string canonical) =>
        Parse(text, "PLN", "en-US").Value.Amount.ToString(CultureInfo.InvariantCulture).ShouldBe(canonical);

    [Theory]
    [InlineData("de-DE", "12.50")]
    [InlineData("de-DE", "1.23")]
    [InlineData("de-DE", "1.2345,00")]
    [InlineData("en-US", "12,50")]
    [InlineData("en-US", "1,23")]
    [InlineData("en-US", "12,34,567.50")]
    [InlineData("en-IN", "1,234,567.50")]
    [InlineData("pl-PL", "12.50")]
    [InlineData("pl-PL", "1.234")]
    [InlineData("fr-FR", "1.234,56")]
    [InlineData("pl-PL", "1 23,00")]
    [InlineData("en-US", "1.234.56")]
    [InlineData("en-US", "12.")]
    [InlineData("en-US", ".5")]
    [InlineData("en-US", "1,234,")]
    [InlineData("en-US", ",123")]
    [InlineData("en-US", "+12")]
    [InlineData("en-US", "--12")]
    [InlineData("en-US", "12-")]
    [InlineData("en-US", "1e3")]
    [InlineData("en-US", "١٢")]
    [InlineData("en-US", "12 PLN")]
    [InlineData("en-US", "$12")]
    [InlineData("en-US", "1 2")]
    [InlineData("en-US", "99999999999999999999999999999999")]
    public void Unreadable_IsAmountInvalid_WithAnExampleInTheLocale(string locale, string text)
    {
        var error = Parse(text, "PLN", locale).Errors.ShouldHaveSingleItem().ShouldBeOfType<AppError>();

        error.Code.ShouldBe(MoneyInput.Invalid);
        error.Kind.ShouldBe(ErrorKind.Validation);
        error.Parameters["example"].ShouldBe(1234.56m.ToString("N2", CultureInfo.GetCultureInfo(locale)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_IsAmountRequired(string? text) =>
        Parse(text, "PLN", "en-US").ShouldFailWith(MoneyInput.Required, ErrorKind.Validation);

    [Theory]
    [InlineData("12.345")]
    [InlineData("0.001")]
    [InlineData("1.0000000000000000000000000000001")]
    [InlineData("0.12345678901234567890123456789012345")]
    public void TooManyDecimals_IsTheKernelsError_NeverRounded(string text)
    {
        var error = Parse(text, "PLN", "en-US").Errors.ShouldHaveSingleItem().ShouldBeOfType<AppError>();

        error.Code.ShouldBe(ErrorCodes.MoneyTooManyDecimals);
        error.Parameters["currency"].ShouldBe("PLN");
        error.Parameters["maxDecimalPlaces"].ShouldBe(2);
    }

    [Fact]
    public void ThreeDecimalsInAPolishAmount_AreTooManyDecimals_NotAThousand() =>
        Parse("1,234", "PLN", "pl-PL").ShouldFailWith(ErrorCodes.MoneyTooManyDecimals, ErrorKind.Validation);

    [Fact]
    public void Example_UsesTheCurrencysPrecision() =>
        ((AppError)Parse("x", "PLN", "pl-PL").Errors[0]).Parameters["example"].ShouldBe("1\u00A0234,56");

    private static Result<Money> Parse(string? text, string currency, string locale) =>
        MoneyInput.Parse(text, Currency.FromCode(currency).Value, CultureInfo.GetCultureInfo(locale));
}
