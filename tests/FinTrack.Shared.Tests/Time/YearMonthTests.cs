using FinTrack.Shared.Kernel.Time;

namespace FinTrack.Shared.Tests.Time;

/// <summary>
/// A business month (plan 1b, B6): years 1 to 9999, months 1 to 12, one spelling <c>yyyy-MM</c>.
/// </summary>
public class YearMonthTests
{
    // --- Construction and range ---

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 12)]
    [InlineData(2026, 11)]
    [InlineData(9999, 1)]
    [InlineData(9999, 12)]
    public void New_InRange_KeepsYearAndMonth(int year, int month)
    {
        var value = new YearMonth(year, month);

        value.Year.ShouldBe(year);
        value.Month.ShouldBe(month);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(10000, 1)]
    [InlineData(2026, 0)]
    [InlineData(2026, 13)]
    [InlineData(2026, -1)]
    public void New_OutOfRange_Throws(int year, int month) =>
        Should.Throw<ArgumentOutOfRangeException>(() => new YearMonth(year, month));

    [Fact]
    public void Default_IsTheFirstMonth()
    {
        default(YearMonth).ShouldBe(YearMonth.MinValue);
        default(YearMonth).ToString().ShouldBe("0001-01");
    }

    [Fact]
    public void MinAndMax_AreTheRangeEdges()
    {
        YearMonth.MinValue.ShouldBe(new YearMonth(1, 1));
        YearMonth.MaxValue.ShouldBe(new YearMonth(9999, 12));
    }

    [Theory]
    [InlineData(2026, 11, 1)]
    [InlineData(2026, 11, 30)]
    [InlineData(2024, 2, 29)]
    [InlineData(1, 1, 1)]
    [InlineData(9999, 12, 31)]
    public void FromDate_IsTheMonthOfTheDate(int year, int month, int day) =>
        YearMonth.FromDate(new DateOnly(year, month, day)).ShouldBe(new YearMonth(year, month));

    [Theory]
    [InlineData(2026, 11)]
    [InlineData(1, 1)]
    [InlineData(9999, 12)]
    public void FirstDay_IsTheFirstOfTheMonth(int year, int month) =>
        new YearMonth(year, month).FirstDay.ShouldBe(new DateOnly(year, month, 1));

    // --- Equality and comparison ---

    [Fact]
    public void Equality_IsByYearAndMonth()
    {
        new YearMonth(2026, 11).ShouldBe(new YearMonth(2026, 11));
        (new YearMonth(2026, 11) == new YearMonth(2026, 11)).ShouldBeTrue();
        (new YearMonth(2026, 11) != new YearMonth(2026, 12)).ShouldBeTrue();
        new YearMonth(2026, 11).ShouldNotBe(new YearMonth(2025, 11));
        new YearMonth(2026, 11).GetHashCode().ShouldBe(new YearMonth(2026, 11).GetHashCode());
    }

    [Fact]
    public void Comparison_OrdersByYearThenMonth()
    {
        var december = new YearMonth(2026, 12);
        var january = new YearMonth(2027, 1);

        (december < january).ShouldBeTrue();
        (december <= january).ShouldBeTrue();
        (january > december).ShouldBeTrue();
        (january >= december).ShouldBeTrue();
        (january < december).ShouldBeFalse();
        (december > january).ShouldBeFalse();
        var alsoDecember = new YearMonth(2026, 12);
        (december <= alsoDecember).ShouldBeTrue();
        (december >= alsoDecember).ShouldBeTrue();
        (december < alsoDecember).ShouldBeFalse();
        (december > alsoDecember).ShouldBeFalse();

        december.CompareTo(january).ShouldBeLessThan(0);
        january.CompareTo(december).ShouldBeGreaterThan(0);
        december.CompareTo(new YearMonth(2026, 12)).ShouldBe(0);

        // A later month in an earlier year still comes first.
        (new YearMonth(2025, 12) < new YearMonth(2026, 1)).ShouldBeTrue();
    }

    [Fact]
    public void Sorting_IsChronological()
    {
        YearMonth[] months = [new(2027, 1), new(2026, 2), new(2026, 12), new(1, 1), new(9999, 12)];

        months.Order().ShouldBe([new(1, 1), new(2026, 2), new(2026, 12), new(2027, 1), new(9999, 12)]);
    }

    // --- Arithmetic ---

    [Theory]
    [InlineData(2026, 11, 2026, 12)]
    [InlineData(2026, 12, 2027, 1)]
    [InlineData(1, 1, 1, 2)]
    [InlineData(9998, 12, 9999, 1)]
    public void Next_AndPrevious_CrossYearBoundaries(int year, int month, int nextYear, int nextMonth)
    {
        var value = new YearMonth(year, month);
        var next = new YearMonth(nextYear, nextMonth);

        value.Next().ShouldBe(next);
        next.Previous().ShouldBe(value);
    }

    [Fact]
    public void Next_OfTheLastMonth_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => YearMonth.MaxValue.Next());

    [Fact]
    public void Previous_OfTheFirstMonth_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => YearMonth.MinValue.Previous());

    [Theory]
    [InlineData(2026, 11, 0, 2026, 11)]
    [InlineData(2026, 11, 2, 2027, 1)]
    [InlineData(2026, 11, -11, 2025, 12)]
    [InlineData(2026, 11, 14, 2028, 1)]
    [InlineData(2026, 1, -1, 2025, 12)]
    [InlineData(2026, 1, -13, 2024, 12)]
    [InlineData(1, 1, 119_987, 9999, 12)]
    [InlineData(9999, 12, -119_987, 1, 1)]
    public void AddMonths_MovesByWholeMonths(int year, int month, int months, int expectedYear, int expectedMonth) =>
        new YearMonth(year, month).AddMonths(months).ShouldBe(new YearMonth(expectedYear, expectedMonth));

    [Theory]
    [InlineData(1, 1, -1)]
    [InlineData(1, 1, 119_988)]
    [InlineData(9999, 12, 1)]
    [InlineData(2026, 11, int.MaxValue)]
    [InlineData(2026, 11, int.MinValue)]
    public void AddMonths_PastTheRange_Throws(int year, int month, int months) =>
        Should.Throw<ArgumentOutOfRangeException>(() => new YearMonth(year, month).AddMonths(months));

    [Theory]
    [InlineData(2026, 11, 2026, 11, 0)]
    [InlineData(2026, 11, 2026, 12, 1)]
    [InlineData(2026, 11, 2027, 1, 2)]
    [InlineData(2027, 1, 2026, 11, -2)]
    [InlineData(2026, 12, 2027, 12, 12)]
    [InlineData(2026, 1, 2025, 12, -1)]
    [InlineData(1, 1, 9999, 12, 119_987)]
    public void MonthsBetween_IsSigned(int fromYear, int fromMonth, int toYear, int toMonth, int expected) =>
        YearMonth.MonthsBetween(new YearMonth(fromYear, fromMonth), new YearMonth(toYear, toMonth)).ShouldBe(expected);

    // --- Text ---

    [Theory]
    [InlineData(2026, 11, "2026-11")]
    [InlineData(2026, 1, "2026-01")]
    [InlineData(1, 1, "0001-01")]
    [InlineData(999, 12, "0999-12")]
    [InlineData(9999, 12, "9999-12")]
    public void ToString_IsYearDashMonth(int year, int month, string text)
    {
        new YearMonth(year, month).ToString().ShouldBe(text);
        YearMonth.TryParse(text, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(new YearMonth(year, month));
    }

    [Fact]
    public void ToString_IgnoresTheCurrentCulture()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // Arabic (Saudi Arabia) uses another calendar and other digits by default.
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            new YearMonth(2026, 11).ToString().ShouldBe("2026-11");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    // Shared with the JSON tests, which send each of them as a JSON string.
    public static TheoryData<string> NotAMonth =>
    [
        "",
        "2026-1",
        "26-11",
        "02026-11",
        "2026-011",
        "2026-11-01",
        "2026/11",
        "2026.11",
        "2026 11",
        "202611",
        " 2026-11",
        "2026-11 ",
        "+2026-11",
        "-2026-11",
        "2026--1",
        "2026-+1",
        "0000-01",
        "0000-12",
        "2026-00",
        "2026-13",
        "2026-99",
        "abcd-ef",
        "2026-1a",
        "2026-\uFF11\uFF11",
        "\uFF12\uFF10\uFF12\uFF16-11",
        "\u0662\u0660\u0662\u0666-11",
        "000\u0662-11", // a single other digit: read as a code point minus '0', it would be a year in range
        "2026-0\u0661",
        "2026-11\0",
        "2026\u201311",
        "2026-11Z",
    ];

    [Theory]
    [MemberData(nameof(NotAMonth))]
    public void TryParse_AnythingButYearDashMonth_Fails(string text)
    {
        YearMonth.TryParse(text, out var parsed).ShouldBeFalse();
        parsed.ShouldBe(default);
    }

    [Fact]
    public void TryParse_Null_Fails() =>
        YearMonth.TryParse(null, out _).ShouldBeFalse();
}
