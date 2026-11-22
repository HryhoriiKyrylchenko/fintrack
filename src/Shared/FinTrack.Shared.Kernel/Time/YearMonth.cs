using System.Globalization;

namespace FinTrack.Shared.Kernel.Time;

/// <summary>
/// A business month: years 1 to 9999, months 1 to 12 (plan 1b, B6). Plans, pauses, limits and
/// repayment plans count in months. Its one spelling is <c>yyyy-MM</c> (<c>2026-11</c>), in JSON and
/// in storage (ARCHITECTURE 2.2 and 2.7).
/// </summary>
/// <remarks>
/// <para>
/// The month of an entry is the month of its <c>OccurredOn</c> (<see cref="FromDate"/>), which is
/// already a business date in the owner's time zone; the current month is the month of the owner's
/// today, computed in Application as today is. A month itself has no time zone.
/// </para>
/// <para>
/// Like <see cref="DateOnly"/>, the default value is the first month of the range (<c>0001-01</c>),
/// and leaving the range throws <see cref="ArgumentOutOfRangeException"/>: months come from dates,
/// from stored documents or from strictly read JSON, so a value out of range is a bug.
/// </para>
/// </remarks>
public readonly record struct YearMonth : IComparable<YearMonth>
{
    private const int MinYear = 1;
    private const int MaxYear = 9999;
    private const int MonthsPerYear = 12;
    private const int MaxIndex = ((MaxYear - MinYear + 1) * MonthsPerYear) - 1;
    private const int TextLength = 7;
    private const char Separator = '-';

    // Months since 0001-01, so the default value is 0001-01 and arithmetic is on one number.
    private readonly int index;

    /// <summary>Creates the month <paramref name="month"/> of <paramref name="year"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The year is not 1 to 9999 or the month not 1 to 12.</exception>
    public YearMonth(int year, int month)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(year, MinYear);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(year, MaxYear);
        ArgumentOutOfRangeException.ThrowIfLessThan(month, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(month, MonthsPerYear);

        index = ((year - MinYear) * MonthsPerYear) + (month - 1);
    }

    private YearMonth(int index) => this.index = index;

    /// <summary>The first month of the range, <c>0001-01</c>; also the default value.</summary>
    public static YearMonth MinValue => default;

    /// <summary>The last month of the range, <c>9999-12</c>.</summary>
    public static YearMonth MaxValue => new(MaxIndex);

    /// <summary>The year, 1 to 9999.</summary>
    public int Year => (index / MonthsPerYear) + MinYear;

    /// <summary>The month of the year, 1 to 12.</summary>
    public int Month => (index % MonthsPerYear) + 1;

    /// <summary>The first day of the month.</summary>
    public DateOnly FirstDay => new(Year, Month, 1);

    /// <summary>The month that contains <paramref name="date"/>.</summary>
    public static YearMonth FromDate(DateOnly date) => new(date.Year, date.Month);

    /// <summary>
    /// The number of months from <paramref name="from"/> to <paramref name="to"/>: positive when
    /// <paramref name="to"/> is later, negative when earlier (2026-11 to 2027-01 is 2, back is -2).
    /// <c>from.AddMonths(MonthsBetween(from, to)) == to</c>.
    /// </summary>
    public static int MonthsBetween(YearMonth from, YearMonth to) => to.index - from.index;

    /// <summary>
    /// Reads a month in its one spelling: exactly four ASCII digits, <c>-</c>, two ASCII digits,
    /// with the year 1 to 9999 and the month 1 to 12. Anything else fails, never converted: another
    /// separator, a day, a sign, spaces, other digits or more or fewer of them.
    /// </summary>
    public static bool TryParse(string? text, out YearMonth result)
    {
        result = default;

        if (text is not { Length: TextLength } || text[4] != Separator)
        {
            return false;
        }

        if (!TryReadDigits(text.AsSpan(0, 4), out var year) || !TryReadDigits(text.AsSpan(5, 2), out var month))
        {
            return false;
        }

        if (year is < MinYear or > MaxYear || month is < 1 or > MonthsPerYear)
        {
            return false;
        }

        result = new YearMonth(year, month);
        return true;
    }

    /// <summary>The month <paramref name="months"/> months later (earlier when negative).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The result is before 0001-01 or after 9999-12.</exception>
    public YearMonth AddMonths(int months)
    {
        var moved = (long)index + months;
        return moved is < 0 or > MaxIndex
            ? throw new ArgumentOutOfRangeException(nameof(months), "The month would be outside 0001-01 to 9999-12.")
            : new YearMonth((int)moved);
    }

    /// <summary>The following month.</summary>
    /// <exception cref="ArgumentOutOfRangeException">This is 9999-12.</exception>
    public YearMonth Next() => AddMonths(1);

    /// <summary>The preceding month.</summary>
    /// <exception cref="ArgumentOutOfRangeException">This is 0001-01.</exception>
    public YearMonth Previous() => AddMonths(-1);

    /// <inheritdoc />
    public int CompareTo(YearMonth other) => index.CompareTo(other.index);

    /// <summary>The month as <c>yyyy-MM</c> (<c>2026-11</c>), whatever the current culture.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Year:D4}{Separator}{Month:D2}");

    /// <summary>Whether <paramref name="left"/> is earlier than <paramref name="right"/>.</summary>
    public static bool operator <(YearMonth left, YearMonth right) => left.index < right.index;

    /// <summary>Whether <paramref name="left"/> is later than <paramref name="right"/>.</summary>
    public static bool operator >(YearMonth left, YearMonth right) => left.index > right.index;

    /// <summary>Whether <paramref name="left"/> is earlier than or the same as <paramref name="right"/>.</summary>
    public static bool operator <=(YearMonth left, YearMonth right) => left.index <= right.index;

    /// <summary>Whether <paramref name="left"/> is later than or the same as <paramref name="right"/>.</summary>
    public static bool operator >=(YearMonth left, YearMonth right) => left.index >= right.index;

    private static bool TryReadDigits(ReadOnlySpan<char> digits, out int value)
    {
        value = 0;
        foreach (var digit in digits)
        {
            if (!char.IsAsciiDigit(digit))
            {
                return false;
            }

            value = (value * 10) + (digit - '0');
        }

        return true;
    }
}
