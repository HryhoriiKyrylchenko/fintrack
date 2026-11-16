using FinTrack.Web.Settings;

namespace FinTrack.Web.Tests.Settings;

/// <summary>
/// "Today" is the date in the owner's time zone, not on the server or in UTC (ARCHITECTURE 3):
/// an entry dated today must not be in the future for the owner.
/// </summary>
public class OwnerDateTests
{
    [Theory]
    // Warsaw is UTC+1 in winter: late evening in UTC is already the next day there.
    [InlineData("2026-01-15T23:30:00Z", "Europe/Warsaw", "2026-01-16")]
    [InlineData("2026-01-15T22:59:59Z", "Europe/Warsaw", "2026-01-15")]
    // New York is UTC-5 in winter: early morning in UTC is still the previous day there.
    [InlineData("2026-01-16T03:00:00Z", "America/New_York", "2026-01-15")]
    // Summer time: Warsaw is UTC+2, so 22:00 UTC is midnight.
    [InlineData("2026-07-15T22:00:00Z", "Europe/Warsaw", "2026-07-16")]
    [InlineData("2026-07-15T21:59:59Z", "Europe/Warsaw", "2026-07-15")]
    // Far from UTC on both sides of the date line.
    [InlineData("2026-01-15T10:00:00Z", "Pacific/Kiritimati", "2026-01-16")]
    [InlineData("2026-01-15T10:00:00Z", "Pacific/Pago_Pago", "2026-01-14")]
    [InlineData("2026-01-15T23:59:59Z", "Etc/UTC", "2026-01-15")]
    public void Today_IsTheDateInTheOwnersTimeZone(string utcNow, string timeZone, string expected) =>
        OwnerDate.Today(DateTimeOffset.Parse(utcNow, System.Globalization.CultureInfo.InvariantCulture), timeZone)
            .ShouldBe(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void Today_AroundTheSpringChange_FollowsTheLocalClock()
    {
        // Warsaw moves from UTC+1 to UTC+2 at 01:00 UTC on 29 March 2026.
        OwnerDate.Today(new DateTimeOffset(2026, 3, 28, 22, 59, 0, TimeSpan.Zero), "Europe/Warsaw").ShouldBe(new DateOnly(2026, 3, 28));
        OwnerDate.Today(new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.Zero), "Europe/Warsaw").ShouldBe(new DateOnly(2026, 3, 29));
    }

    [Theory]
    [InlineData("2026-11-16T20:10:00Z", "America/New_York", "2026-11-16T15:10:00")]
    [InlineData("2026-11-16T20:10:00Z", "Europe/Warsaw", "2026-11-16T21:10:00")]
    [InlineData("2026-07-01T00:30:00Z", "Europe/Warsaw", "2026-07-01T02:30:00")]
    public void LocalTime_IsTheWallClockInTheOwnersTimeZone_NotTheBrowsers(string instant, string timeZone, string expected) =>
        OwnerDate.LocalTime(DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture), timeZone)
            .ShouldBe(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void Today_WithAnUnknownTimeZone_IsABug() =>
        Should.Throw<TimeZoneNotFoundException>(() => OwnerDate.Today(DateTimeOffset.UnixEpoch, "Mars/Olympus"));
}
