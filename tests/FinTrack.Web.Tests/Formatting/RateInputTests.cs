using System.Globalization;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;
using FinTrack.Web.Formatting;

namespace FinTrack.Web.Tests.Formatting;

/// <summary>A rate is read like an amount (the locale's own syntax), must be above zero, and is never rounded.</summary>
public class RateInputTests
{
    [Theory]
    [InlineData("pl-PL", "4,2735", "4.2735")]
    [InlineData("en-US", "0.232558139535", "0.232558139535")]
    [InlineData("de-DE", "1.234,5", "1234.5")]
    [InlineData("en-US", "  1  ", "1")]
    public void Rate_IsReadExactly(string locale, string text, string expected) =>
        RateInput.Parse(text, CultureInfo.GetCultureInfo(locale)).Value.ShouldBe(decimal.Parse(expected, CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("pl-PL", "4.2735")]
    [InlineData("en-US", "4,2735")]
    [InlineData("en-US", "0")]
    [InlineData("en-US", "0.000")]
    [InlineData("en-US", "-1")]
    [InlineData("en-US", "abc")]
    [InlineData("en-US", "0.00000000000000000000000000001")]
    public void NotARateAboveZero_IsRateInvalid_WithAnExample(string locale, string text)
    {
        var error = RateInput.Parse(text, CultureInfo.GetCultureInfo(locale)).ShouldFailWith(RateInput.Invalid, ErrorKind.Validation);

        error.Parameters["example"].ShouldBe(4.2735m.ToString("N4", CultureInfo.GetCultureInfo(locale)));
    }

    [Theory]
    [InlineData("pl-PL", "0.232558139535", "0,232558")]
    [InlineData("en-US", "4.2735", "4.2735")]
    [InlineData("en-US", "1", "1")]
    [InlineData("de-DE", "1234.5", "1.234,5")]
    public void Format_ShowsAtMostSixDecimals_InTheLocale(string locale, string rate, string expected) =>
        RateInput.Format(decimal.Parse(rate, CultureInfo.InvariantCulture), CultureInfo.GetCultureInfo(locale)).ShouldBe(expected);

    [Fact]
    public void Empty_IsRateRequired() =>
        RateInput.Parse("  ", CultureInfo.InvariantCulture).ShouldFailWith(RateInput.Required, ErrorKind.Validation);
}
