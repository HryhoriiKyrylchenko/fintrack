using System.Globalization;

namespace FinTrack.Web.Settings;

/// <summary>
/// Applies the owner's locale to formatting (numbers, dates, money). Texts stay English (DESIGN 7),
/// so only the formatting culture changes, never the UI culture of the resource files.
/// </summary>
public static class OwnerCulture
{
    /// <summary>
    /// Makes <paramref name="locale"/> the formatting culture of the app. Returns <c>false</c> and
    /// changes nothing when the culture data is not available here.
    /// </summary>
    public static bool TryApply(string locale)
    {
        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(locale, predefinedOnly: true);
        }
        catch (CultureNotFoundException)
        {
            return false;
        }

        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.CurrentCulture = culture;
        return true;
    }
}

/// <summary>The browser's own time zone and language, read before the owner's locale is applied; the first-run form starts from them.</summary>
/// <param name="TimeZone">The browser's time zone (IANA ID).</param>
/// <param name="Locale">The browser's preferred language, for example <c>pl-PL</c>.</param>
public sealed record BrowserDefaults(string TimeZone, string Locale);
