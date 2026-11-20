using CsCheck;
using FinTrack.Shared.Kernel.Monetary;

namespace FinTrack.Shared.Tests.Monetary;

/// <summary>
/// Properties of <see cref="Money"/> that must hold for every currency precision (0 to 18)
/// and every amount. Groundwork for invariants 9 and 11 (ARCHITECTURE section 7).
/// </summary>
public class MoneyPropertyTests
{
    // Currencies with every allowed precision.
    private static readonly Gen<Currency> Currencies =
        Gen.Int[0, Currency.MaxDecimalPlaces].Select(places => Currency.Define($"T{places}", places));

    // A valid amount: a whole number of minor units, so it always fits the currency.
    // Small amounts are generated half of the time, because rounding edge cases
    // (a few minor units spread over many parts) live there.
    private static Gen<Money> AmountsIn(Currency currency) =>
        Gen.OneOf(Gen.Long[-100, 100], Gen.Long[-1_000_000_000_000_000, 1_000_000_000_000_000])
            .Select(units => Money.From(units / Pow10(currency.DecimalPlaces), currency));

    private static readonly Gen<(Money Amount, decimal[] Weights)> Splits =
        Currencies.SelectMany(currency => AmountsIn(currency))
            .Select(Gen.Decimal[0.0001m, 1_000m].Array[1, 12], (amount, weights) => (amount, weights));

    [Fact]
    public void Split_PartsSumExactlyToTheAmount() =>
        Splits.Sample(split =>
        {
            var parts = split.Amount.Split(split.Weights);
            return parts.Count == split.Weights.Length
                && parts.Aggregate(Money.Zero(split.Amount.Currency), (sum, part) => sum + part) == split.Amount;
        });

    [Fact]
    public void Split_PartsNeverHaveTheOppositeSign() =>
        Splits.Sample(split => split.Amount.Split(split.Weights)
            .All(part => part.Amount == 0m || Math.Sign(part.Amount) == Math.Sign(split.Amount.Amount)));

    [Fact]
    public void Split_PartsStayAtCurrencyPrecision() =>
        Splits.Sample(split => split.Amount.Split(split.Weights)
            .All(part => FitsPrecision(part.Amount, part.Currency)));

    [Fact]
    public void EveryResult_HasExactlyTheCurrencysScale() =>
        Currencies.SelectMany(currency => Gen.Select(AmountsIn(currency), AmountsIn(currency), Gen.Decimal[0.0001m, 1_000m].Array[1, 12]))
            .Sample((a, b, weights) =>
                new[] { a, b, a + b, a - b, -a, Money.Round(a.Amount / 7m, a.Currency) }.Concat(a.Split(weights))
                    .All(money => money.Amount.Scale == money.Currency.DecimalPlaces));

    [Fact]
    public void AddThenSubtract_ReturnsTheOriginal() =>
        Currencies.SelectMany(currency => Gen.Select(AmountsIn(currency), AmountsIn(currency)))
            .Sample((a, b) => a + b - b == a);

    [Fact]
    public void Round_StaysAtPrecisionAndWithinHalfAUnit() =>
        Gen.Select(Currencies, Gen.Decimal[-1_000_000m, 1_000_000m])
            .Sample((currency, raw) =>
            {
                var rounded = Money.Round(raw, currency);
                var halfUnit = 0.5m / Pow10(currency.DecimalPlaces);
                return FitsPrecision(rounded.Amount, currency) && Math.Abs(rounded.Amount - raw) <= halfUnit;
            });

    [Fact]
    public void Convert_ResultStaysAtTargetPrecision() =>
        Gen.Select(Currencies, Currencies, Gen.Decimal[0.000001m, 10_000m])
            .SelectMany((from, to, rate) => AmountsIn(from).Select(amount => (amount, to, rate)))
            .Sample(x => FitsPrecision(x.amount.Convert(x.rate, x.to).Amount, x.to));

    [Fact]
    public void MixedCurrencies_AlwaysThrow() =>
        Gen.Select(Currencies, Currencies)
            .Where((left, right) => left != right)
            .SelectMany((left, right) => Gen.Select(AmountsIn(left), AmountsIn(right)))
            .Sample((a, b) =>
                Throws(() => _ = a + b)
                && Throws(() => _ = a - b)
                && Throws(() => _ = a < b)
                && Throws(() => _ = a >= b));

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (CurrencyMismatchException)
        {
            return true;
        }
    }

    private static bool FitsPrecision(decimal amount, Currency currency) =>
        decimal.Round(amount, currency.DecimalPlaces) == amount;

    private static decimal Pow10(int exponent)
    {
        var result = 1m;
        for (var i = 0; i < exponent; i++)
        {
            result *= 10m;
        }

        return result;
    }
}
