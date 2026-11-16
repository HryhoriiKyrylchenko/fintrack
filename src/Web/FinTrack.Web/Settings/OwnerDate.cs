namespace FinTrack.Web.Settings;

/// <summary>
/// Dates as the owner sees them. "Today" is the date in the owner's time zone (ARCHITECTURE 3),
/// so an entry dated today is never in the future for the owner, wherever the server runs.
/// </summary>
public static class OwnerDate
{
    /// <summary>The date at <paramref name="utcNow"/> in <paramref name="timeZone"/> (an IANA ID from the saved settings).</summary>
    /// <exception cref="TimeZoneNotFoundException">The time zone is unknown here; saved settings always hold a known one.</exception>
    public static DateOnly Today(DateTimeOffset utcNow, string timeZone) =>
        DateOnly.FromDateTime(LocalTime(utcNow, timeZone));

    /// <summary>
    /// The wall-clock time of <paramref name="instant"/> in <paramref name="timeZone"/>, for showing an
    /// instant to the owner. The owner's time zone, not the browser's: the two may differ.
    /// </summary>
    /// <exception cref="TimeZoneNotFoundException">The time zone is unknown here; saved settings always hold a known one.</exception>
    public static DateTime LocalTime(DateTimeOffset instant, string timeZone) =>
        TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(timeZone)).DateTime;
}
