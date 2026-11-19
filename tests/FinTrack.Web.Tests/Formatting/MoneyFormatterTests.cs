using System.Globalization;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Web.Formatting;

namespace FinTrack.Web.Tests.Formatting;

/// <summary>
/// DESIGN 6: an amount always shows its currency code, has its currency's precision, uses the owner's
/// locale for separators and for where the code goes, and a negative amount starts with the real minus sign.
/// </summary>
public class MoneyFormatterTests
{
    // Spelled out so the expected texts below show which space each locale uses.
    private const char Nbsp = '\u00A0';
    private const char NarrowNbsp = '\u202F';
    private const char Minus = '\u2212';

    public static TheoryData<string, string, decimal, string> Cases => new()
    {
        { "pl-PL", "PLN", 1234.5m, $"1{Nbsp}234,50{Nbsp}PLN" },
        { "pl-PL", "EUR", 0.07m, $"0,07{Nbsp}EUR" },
        { "pl-PL", "USD", 1234567.89m, $"1{Nbsp}234{Nbsp}567,89{Nbsp}USD" },
        { "en-US", "PLN", 1234.5m, $"PLN{Nbsp}1,234.50" },
        { "en-US", "EUR", 12m, $"EUR{Nbsp}12.00" },
        { "en-US", "USD", 1234567.89m, $"USD{Nbsp}1,234,567.89" },
        { "de-DE", "PLN", 1234.5m, $"1.234,50{Nbsp}PLN" },
        { "de-DE", "EUR", 999.99m, $"999,99{Nbsp}EUR" },
        { "de-DE", "USD", 1000m, $"1.000,00{Nbsp}USD" },
        { "fr-FR", "EUR", 1234.5m, $"1{NarrowNbsp}234,50{Nbsp}EUR" },
        { "en-IN", "USD", 1234567.5m, $"USD{Nbsp}12,34,567.50" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Amount_FollowsTheLocale_WithTheCurrencysPrecision(string locale, string currency, decimal amount, string expected) =>
        Format(amount, currency, locale).ShouldBe(expected);

    [Fact]
    public void Grouping_UsesTheLocalesOwnSeparator_WhateverTheCultureDataSays()
    {
        // de-CH groups with an apostrophe whose exact character depends on the ICU version (U+2019 on
        // Windows, U+0027 on the Linux CI runner); the code goes before the number.
        var separator = CultureInfo.GetCultureInfo("de-CH").NumberFormat.CurrencyGroupSeparator;

        Format(1234.5m, "EUR", "de-CH").ShouldBe($"EUR{Nbsp}1{separator}234.50");
    }

    [Theory]
    [InlineData("pl-PL", "\u22121\u00A0234,50\u00A0PLN")]
    [InlineData("en-US", "\u2212PLN\u00A01,234.50")]
    [InlineData("de-DE", "\u22121.234,50\u00A0PLN")]
    [InlineData("sv-SE", "\u22121\u00A0234,50\u00A0PLN")]
    public void Negative_StartsWithTheRealMinusSign(string locale, string expected) =>
        Format(-1234.5m, "PLN", locale).ShouldBe(expected);

    [Fact]
    public void Signed_ShowsPlusForPositive_MinusForNegative_AndNothingForZero()
    {
        var culture = CultureInfo.GetCultureInfo("pl-PL");

        MoneyFormatter.Format(Money.From(12.3m, Currency.Pln), culture, MoneySign.Always).ShouldBe($"+12,30{Nbsp}PLN");
        MoneyFormatter.Format(Money.From(-12.3m, Currency.Pln), culture, MoneySign.Always).ShouldBe($"{Minus}12,30{Nbsp}PLN");
        MoneyFormatter.Format(Money.Zero(Currency.Pln), culture, MoneySign.Always).ShouldBe($"0,00{Nbsp}PLN");
    }

    [Fact]
    public void Zero_HasNoSign_AndTheFullPrecision() => Format(0m, "EUR", "en-US").ShouldBe($"EUR{Nbsp}0.00");

    [Fact]
    public void TheCulturesOwnCurrencySymbol_IsNeverUsed()
    {
        Format(5m, "EUR", "pl-PL").ShouldNotContain("zł");
        Format(5m, "PLN", "en-US").ShouldNotContain("$");
    }

    [Fact]
    public void AmountWithFewerDecimals_IsPaddedToThePrecision_NeverRounded() =>
        Format(7.1m, "USD", "en-US").ShouldBe($"USD{Nbsp}7.10");

    private static string Format(decimal amount, string currency, string locale) =>
        MoneyFormatter.Format(Money.From(amount, Currency.FromCode(currency).Value), CultureInfo.GetCultureInfo(locale));
}
