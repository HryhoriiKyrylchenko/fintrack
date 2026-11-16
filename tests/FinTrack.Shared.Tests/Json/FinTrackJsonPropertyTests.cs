using System.Globalization;
using System.Text.Json;
using CsCheck;
using FinTrack.Shared.Kernel.Json;
using FinTrack.Shared.Kernel.Monetary;

namespace FinTrack.Shared.Tests.Json;

/// <summary>
/// Amounts cross the wire exactly: every decimal, with every scale up to the 18 places of a
/// crypto quantity and beyond, comes back as the same value with the same scale (ARCHITECTURE 2.7).
/// </summary>
public class FinTrackJsonPropertyTests
{
    private static readonly Currency[] Registered = [Currency.Pln, Currency.Eur, Currency.Usd];

    // A whole number of units at a scale of 0 to 28: covers trailing zeros ("1.10") and 18-place quantities.
    private static readonly Gen<decimal> ScaledDecimals =
        Gen.Select(Gen.ULong, Gen.Bool, Gen.Int[0, 28])
            .Select((units, negative, scale) => new decimal((int)(uint)units, (int)(uint)(units >> 32), 0, negative, (byte)scale));

    [Fact]
    public void Decimal_RoundTripsExactly_WithItsScale() =>
        Gen.OneOf(ScaledDecimals, Gen.Decimal).Sample(value =>
        {
            var json = JsonSerializer.Serialize(value, FinTrackJson.Options);
            var read = JsonSerializer.Deserialize<decimal>(json, FinTrackJson.Options);

            return json == $"\"{value.ToString(CultureInfo.InvariantCulture)}\""
                && read == value
                && read.Scale == value.Scale;
        });

    [Fact]
    public void Money_RoundTripsExactly() =>
        Gen.Select(Gen.Int[0, Registered.Length - 1], Gen.Long[-1_000_000_000_000_000, 1_000_000_000_000_000], Gen.Bool)
            .Sample((index, units, trailingZero) =>
            {
                var currency = Registered[index];
                var amount = units / 100m;
                var money = Money.From(trailingZero ? decimal.Round(amount, 1) * 1.0m : amount, currency);

                var read = JsonSerializer.Deserialize<Money>(JsonSerializer.Serialize(money, FinTrackJson.Options), FinTrackJson.Options);

                return read == money && read!.Amount.Scale == money.Amount.Scale;
            });
}
