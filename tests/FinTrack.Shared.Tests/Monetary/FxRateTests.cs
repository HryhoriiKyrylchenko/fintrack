using FinTrack.Shared.Kernel.Monetary;

namespace FinTrack.Shared.Tests.Monetary;

/// <summary>An exchange rate between two currencies on one rate date (ARCHITECTURE 3, plan 1a D8).</summary>
public class FxRateTests
{
    private static readonly DateOnly RateDate = new(2026, 1, 15);

    [Fact]
    public void Convert_UsesTheRate_AndRoundsOnceToTheTargetCurrency()
    {
        var rate = new FxRate(Currency.Eur, Currency.Pln, 4.2345m, RateDate);

        rate.Convert(Money.From(10.01m, Currency.Eur)).ShouldBe(Money.From(42.39m, Currency.Pln));
    }

    [Fact]
    public void Convert_RoundsTheMidpointToEven()
    {
        // 0.125 PLN rounds to 0.12, 0.135 to 0.14 (banker's rounding, ARCHITECTURE 3).
        var rate = new FxRate(Currency.Eur, Currency.Pln, 0.125m, RateDate);

        rate.Convert(Money.From(1m, Currency.Eur)).ShouldBe(Money.From(0.12m, Currency.Pln));
        rate.Convert(Money.From(1.08m, Currency.Eur)).ShouldBe(Money.From(0.14m, Currency.Pln));
    }

    [Fact]
    public void Convert_OfAnotherCurrency_IsABug() =>
        Should.Throw<CurrencyMismatchException>(() => new FxRate(Currency.Eur, Currency.Pln, 4m, RateDate).Convert(Money.From(1m, Currency.Usd)));

    [Theory]
    [InlineData("0")]
    [InlineData("-1.5")]
    public void ARate_IsAboveZero(string rate) =>
        Should.Throw<ArgumentOutOfRangeException>(() => new FxRate(Currency.Eur, Currency.Pln, decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture), RateDate));

    [Fact]
    public void Currencies_AreRequired()
    {
        Should.Throw<ArgumentNullException>(() => new FxRate(null!, Currency.Pln, 1m, RateDate));
        Should.Throw<ArgumentNullException>(() => new FxRate(Currency.Eur, null!, 1m, RateDate));
    }

    [Fact]
    public void All_ListsEveryRegisteredCurrency_Once() =>
        Currency.All.Select(currency => currency.Code).ShouldBe(["EUR", "PLN", "USD"], ignoreOrder: true);
}
