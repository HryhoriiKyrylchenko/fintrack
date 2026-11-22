using System.Text.Json;
using CsCheck;
using FinTrack.Shared.Kernel.Json;
using FinTrack.Shared.Kernel.Time;

namespace FinTrack.Shared.Tests.Time;

/// <summary>
/// Properties of <see cref="YearMonth"/> over the whole range: one spelling that round trips, and
/// arithmetic that agrees with itself, checked against an independent count of year and month.
/// </summary>
public class YearMonthPropertyTests
{
    // Months near the range edges and near today are generated more often: the edges are where
    // off-by-one errors live.
    private static readonly Gen<YearMonth> Months =
        Gen.Select(Gen.OneOf(Gen.Int[1, 9999], Gen.Int[1, 3], Gen.Int[9997, 9999], Gen.Int[2020, 2030]), Gen.Int[1, 12])
            .Select((year, month) => new YearMonth(year, month));

    private static int Count(YearMonth value) => (value.Year * 12) + value.Month;

    [Fact]
    public void Text_RoundTrips() =>
        Months.Sample(value =>
        {
            var text = value.ToString();
            return text.Length == 7
                && YearMonth.TryParse(text, out var parsed)
                && parsed == value
                && text == $"{value.Year:D4}-{value.Month:D2}";
        });

    [Fact]
    public void Json_RoundTrips() =>
        Months.Sample(value =>
        {
            var json = JsonSerializer.Serialize(value, FinTrackJson.Options);
            return json == $"\"{value}\"" && JsonSerializer.Deserialize<YearMonth>(json, FinTrackJson.Options) == value;
        });

    [Fact]
    public void FromDate_ThenFirstDay_IsTheFirstOfTheDatesMonth() =>
        Gen.Date.Select(date => DateOnly.FromDateTime(date)).Sample(date =>
            YearMonth.FromDate(date).FirstDay == new DateOnly(date.Year, date.Month, 1));

    [Fact]
    public void NextThenPrevious_IsTheIdentity() =>
        Months.Where(value => value != YearMonth.MaxValue).Sample(value =>
            value.Next().Previous() == value && Count(value.Next()) == Count(value) + 1);

    [Fact]
    public void PreviousThenNext_IsTheIdentity() =>
        Months.Where(value => value != YearMonth.MinValue).Sample(value =>
            value.Previous().Next() == value && Count(value.Previous()) == Count(value) - 1);

    [Fact]
    public void MonthsBetween_AgreesWithAddMonths() =>
        Gen.Select(Months, Months).Sample((from, to) =>
        {
            var between = YearMonth.MonthsBetween(from, to);
            return between == Count(to) - Count(from)
                && from.AddMonths(between) == to
                && to.AddMonths(-between) == from
                && YearMonth.MonthsBetween(to, from) == -between;
        });

    [Fact]
    public void Comparison_AgreesWithMonthsBetween() =>
        Gen.Select(Months, Months).Sample((left, right) =>
        {
            var between = YearMonth.MonthsBetween(left, right);
            return Math.Sign(left.CompareTo(right)) == -Math.Sign(between)
                && (left < right) == (between > 0)
                && (left == right) == (between == 0)
                && (left >= right) == (between <= 0);
        });
}
