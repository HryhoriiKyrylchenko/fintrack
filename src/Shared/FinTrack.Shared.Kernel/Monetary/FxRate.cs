namespace FinTrack.Shared.Kernel.Monetary;

/// <summary>
/// An exchange rate from <see cref="From"/> to <see cref="To"/>, valid on its rate date
/// (ARCHITECTURE 3, plan 1a D8). Totals across currencies use it at read time and always show
/// <see cref="Date"/>; a converted amount is never stored as truth.
/// </summary>
/// <remarks>
/// The rate is not rounded: a cross rate through EUR is the quotient at <see cref="decimal"/>
/// precision. Rounding happens once, in <see cref="Convert"/>, to the target currency's precision.
/// </remarks>
public sealed record FxRate
{
    /// <summary>A rate; <paramref name="rate"/> is the number of units of <paramref name="to"/> per unit of <paramref name="from"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The rate is zero or negative.</exception>
    public FxRate(Currency from, Currency to, decimal rate, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate);

        From = from;
        To = to;
        Rate = rate;
        Date = date;
    }

    /// <summary>The currency converted from.</summary>
    public Currency From { get; }

    /// <summary>The currency converted to.</summary>
    public Currency To { get; }

    /// <summary>Units of <see cref="To"/> per unit of <see cref="From"/>; always above zero.</summary>
    public decimal Rate { get; }

    /// <summary>The day the rate was published for (the rate date), not the day it was fetched.</summary>
    public DateOnly Date { get; }

    /// <summary>Converts <paramref name="amount"/> to <see cref="To"/>, rounded once with banker's rounding.</summary>
    /// <exception cref="CurrencyMismatchException">The amount is not in <see cref="From"/> (a bug).</exception>
    public Money Convert(Money amount)
    {
        ArgumentNullException.ThrowIfNull(amount);

        if (amount.Currency != From)
        {
            throw new CurrencyMismatchException(amount.Currency, From);
        }

        return amount.Convert(Rate, To);
    }
}
