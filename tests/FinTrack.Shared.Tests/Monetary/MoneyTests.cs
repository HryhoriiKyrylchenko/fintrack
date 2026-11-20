using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;

namespace FinTrack.Shared.Tests.Monetary;

/// <summary>Worked examples for <see cref="Money"/>. Invented numbers only.</summary>
public class MoneyTests
{
    private static Money Pln(decimal amount) => Money.From(amount, Currency.Pln);

    private static Money Eur(decimal amount) => Money.From(amount, Currency.Eur);

    private static string Text(Money money) => money.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // --- Creation ---

    [Theory]
    [InlineData("10")]
    [InlineData("10.5")]
    [InlineData("10.55")]
    [InlineData("10.500")] // trailing zeros are not extra precision
    [InlineData("-3.20")]
    [InlineData("0")]
    public void Create_WithinPrecision_Succeeds(string amount)
    {
        var result = Money.Create(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), Currency.Pln);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Amount.ShouldBe(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture));
        result.Value.Currency.ShouldBe(Currency.Pln);
    }

    [Theory]
    [InlineData("10.555")]
    [InlineData("0.001")]
    [InlineData("-0.005")]
    public void Create_MoreDecimalsThanCurrencyAllows_Fails(string amount)
    {
        var result = Money.Create(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), Currency.Pln);

        // The amount itself is deliberately not a parameter: error parameters may end up in logs.
        result.ShouldFailWith(ErrorCodes.MoneyTooManyDecimals, ErrorKind.Validation)
            .Parameters.ShouldBe(new Dictionary<string, object> { ["currency"] = "PLN", ["maxDecimalPlaces"] = 2 });
    }

    [Fact]
    public void Create_NullCurrency_Throws() =>
        Should.Throw<ArgumentNullException>(() => Money.Create(1m, null!));

    [Fact]
    public void From_MoreDecimalsThanCurrencyAllows_Throws() =>
        Should.Throw<ArgumentException>(() => Money.From(1.001m, Currency.Pln));

    [Fact]
    public void Zero_IsZeroInThatCurrency()
    {
        var zero = Money.Zero(Currency.Eur);

        zero.Amount.ShouldBe(0m);
        zero.Currency.ShouldBe(Currency.Eur);
    }

    // --- Scale: an amount always has exactly its currency's decimal places, so it has one spelling ---

    [Theory]
    [InlineData("50", "50.00")]
    [InlineData("1.1", "1.10")]
    [InlineData("1.100", "1.10")] // trailing zeros beyond the precision are dropped
    [InlineData("-3.2", "-3.20")]
    [InlineData("0", "0.00")]
    [InlineData("-0.000", "0.00")] // zero has no sign
    public void FromAndCreate_HaveTheCurrencysScale(string amount, string expected)
    {
        var value = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);

        Text(Money.From(value, Currency.Pln)).ShouldBe(expected);
        Text(Money.Create(value, Currency.Pln).Value).ShouldBe(expected);
    }

    [Fact]
    public void Zero_HasTheCurrencysScale()
    {
        Text(Money.Zero(Currency.Pln)).ShouldBe("0.00");
        Text(Money.Zero(Currency.Define("WHL", 0))).ShouldBe("0");
        Text(Money.Zero(Currency.Define("Q18", 18))).ShouldBe("0.000000000000000000");
    }

    [Fact]
    public void ZeroDecimalCurrency_HasNoDecimals() => Text(Money.From(2.0m, Currency.Define("WHL", 0))).ShouldBe("2");

    [Fact]
    public void Results_HaveTheCurrencysScale()
    {
        Text(Pln(1.5m) + Pln(2m)).ShouldBe("3.50");
        Text(Pln(2m) - Pln(2m)).ShouldBe("0.00");
        Text(-Pln(1m)).ShouldBe("-1.00");
        Text(Money.Round(7m, Currency.Pln)).ShouldBe("7.00");
        Text(Eur(100m).Convert(2m, Currency.Pln)).ShouldBe("200.00");
        Pln(100m).Split([1m, 4m]).Select(Text).ShouldBe(["20.00", "80.00"]);
    }

    [Fact]
    public void NegatedZero_IsPositiveZero()
    {
        var zero = -Money.Zero(Currency.Pln);

        Text(zero).ShouldBe("0.00");
        (decimal.GetBits(zero.Amount)[3] & int.MinValue).ShouldBe(0);
    }

    // --- Arithmetic ---

    [Fact]
    public void Add_SameCurrency_AddsAmounts() => (Pln(100.25m) + Pln(0.75m)).ShouldBe(Pln(101m));

    [Fact]
    public void Subtract_SameCurrency_SubtractsAmounts() => (Pln(100m) - Pln(150.50m)).ShouldBe(Pln(-50.50m));

    [Fact]
    public void Negate_FlipsSign() => (-Pln(12.34m)).ShouldBe(Pln(-12.34m));

    [Fact]
    public void Add_DifferentCurrencies_Throws()
    {
        var exception = Should.Throw<CurrencyMismatchException>(() => Pln(1m) + Eur(1m));

        exception.Left.ShouldBe(Currency.Pln);
        exception.Right.ShouldBe(Currency.Eur);
    }

    [Fact]
    public void Subtract_DifferentCurrencies_Throws() =>
        Should.Throw<CurrencyMismatchException>(() => Pln(1m) - Eur(1m));

    // --- Comparison and equality ---

    [Fact]
    public void Comparison_SameCurrency_ComparesAmounts()
    {
        (Pln(1m) < Pln(2m)).ShouldBeTrue();
        (Pln(2m) > Pln(1m)).ShouldBeTrue();
        (Pln(2m) <= Pln(2m)).ShouldBeTrue();
        (Pln(2m) >= Pln(3m)).ShouldBeFalse();
    }

    [Fact]
    public void Comparison_OfEqualAmounts_IsNeitherLessNorGreater()
    {
        // Equal amounts with different scales are equal (2 = 2.00).
        (Pln(2m) < Pln(2.00m)).ShouldBeFalse();
        (Pln(2m) > Pln(2.00m)).ShouldBeFalse();
        (Pln(2m) <= Pln(2.00m)).ShouldBeTrue();
        (Pln(2m) >= Pln(2.00m)).ShouldBeTrue();
    }

    [Fact]
    public void Comparison_DifferentCurrencies_Throws()
    {
        Should.Throw<CurrencyMismatchException>(() => Pln(1m) < Eur(2m));
        Should.Throw<CurrencyMismatchException>(() => Pln(1m) >= Eur(2m));
    }

    [Fact]
    public void CurrencyMismatch_NamesBothCurrencies() =>
        Should.Throw<CurrencyMismatchException>(() => Pln(1m) + Eur(2m)).Message.ShouldBe(
            "Amounts in PLN and EUR cannot be combined without an explicit conversion.");

    [Fact]
    public void Equality_IgnoresTrailingZeros() => Pln(1.5m).ShouldBe(Pln(1.50m));

    [Fact]
    public void Equality_DifferentCurrencies_IsFalseNotAnError()
    {
        (Pln(1m) == Eur(1m)).ShouldBeFalse();
        Pln(1m).Equals(Eur(1m)).ShouldBeFalse();
    }

    [Fact]
    public void ToString_ShowsAmountAndCode() => Pln(-1234.5m).ToString().ShouldBe("-1234.50 PLN");

    // --- Rounding: banker's rounding at the currency's precision ---

    [Theory]
    [InlineData("2.345", "2.34")] // midpoint, 4 is even: down
    [InlineData("2.355", "2.36")] // midpoint, 5 is odd: up
    [InlineData("2.3451", "2.35")] // not a midpoint
    [InlineData("-2.345", "-2.34")]
    [InlineData("-2.355", "-2.36")]
    [InlineData("7", "7")]
    public void Round_UsesBankersRounding(string raw, string expected) =>
        Money.Round(decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture), Currency.Pln)
            .ShouldBe(Pln(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("2.5", "2")]
    [InlineData("3.5", "4")]
    [InlineData("-2.5", "-2")]
    public void Round_ZeroDecimalCurrency_RoundsToWholeUnits(string raw, string expected)
    {
        var wholeUnits = Currency.Define("WHL", 0);

        Money.Round(decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture), wholeUnits).Amount
            .ShouldBe(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    // --- Conversion: explicit rate, rounded at the target precision ---

    [Fact]
    public void Convert_MultipliesByRateAndRoundsToTarget() =>
        Eur(100m).Convert(4.25375m, Currency.Pln).ShouldBe(Pln(425.38m)); // 425.375 -> 425.38 (7 is odd)

    [Fact]
    public void Convert_MidpointRoundsToEven() =>
        Eur(1m).Convert(4.125m, Currency.Pln).ShouldBe(Pln(4.12m));

    [Theory]
    [InlineData("0")]
    [InlineData("-1.5")]
    public void Convert_NonPositiveRate_Throws(string rate) =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            Eur(1m).Convert(decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture), Currency.Pln));

    // --- Split: parts truncated toward zero, remainder to the first part ---

    [Fact]
    public void Split_EqualWeights_RemainderGoesToFirstPart() =>
        Pln(100m).Split([1m, 1m, 1m]).ShouldBe([Pln(33.34m), Pln(33.33m), Pln(33.33m)]);

    [Fact]
    public void Split_UnequalWeights_FollowsWeights() =>
        Pln(1000m).Split([3m, 1m]).ShouldBe([Pln(750m), Pln(250m)]);

    // With two parts, any wrong share of the second part is hidden: the first part takes the rest.
    [Fact]
    public void Split_ThreeUnequalWeights_EachPartFollowsItsWeight() =>
        Pln(100m).Split([1m, 2m, 2m]).ShouldBe([Pln(20m), Pln(40m), Pln(40m)]);

    // With rounding to nearest, each large part (0.0167) would round up to 0.02 and the
    // first part would have to become -0.01 for the sum to stay exact.
    [Fact]
    public void Split_TinyFirstWeight_NeverFlipsSign() =>
        Pln(0.05m).Split([0.0001m, 1m, 1m, 1m]).ShouldBe([Pln(0.02m), Pln(0.01m), Pln(0.01m), Pln(0.01m)]);

    [Fact]
    public void Split_NegativeAmount_PartsStayNegative() =>
        Pln(-100m).Split([1m, 1m, 1m]).ShouldBe([Pln(-33.34m), Pln(-33.33m), Pln(-33.33m)]);

    [Fact]
    public void Split_SingleWeight_ReturnsWholeAmount() =>
        Pln(12.34m).Split([5m]).ShouldBe([Pln(12.34m)]);

    [Fact]
    public void Split_Zero_ReturnsZeros() =>
        Money.Zero(Currency.Pln).Split([1m, 2m]).ShouldBe([Pln(0m), Pln(0m)]);

    [Fact]
    public void Split_NoWeights_Throws() =>
        Should.Throw<ArgumentException>(() => Pln(1m).Split([]));

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Split_NonPositiveWeight_Throws(string weight) =>
        Should.Throw<ArgumentException>(() =>
            Pln(1m).Split([1m, decimal.Parse(weight, System.Globalization.CultureInfo.InvariantCulture)]));
}
