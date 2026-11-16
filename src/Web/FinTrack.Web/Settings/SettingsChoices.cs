using System.Globalization;

namespace FinTrack.Web.Settings;

/// <summary>
/// The time zones and locales the settings form offers: what this runtime knows. In the browser
/// that is the IANA time zone data and the ICU culture data shipped with the app.
/// </summary>
public static class SettingsChoices
{
    private static readonly Lazy<IReadOnlyList<string>> TimeZoneIds = new(() =>
        [.. TimeZoneInfo.GetSystemTimeZones().Where(zone => zone.HasIanaId).Select(zone => zone.Id).Order(StringComparer.Ordinal)]);

    private static readonly Lazy<IReadOnlyList<LocaleChoice>> LocaleList = new(() =>
        [.. CultureInfo.GetCultures(CultureTypes.AllCultures)
            .Where(culture => !string.IsNullOrEmpty(culture.Name))
            .Select(culture => new LocaleChoice(culture.Name, culture.EnglishName))
            .OrderBy(choice => choice.DisplayName, StringComparer.Ordinal)]);

    /// <summary>IANA time zone IDs, sorted.</summary>
    public static IReadOnlyList<string> TimeZones => TimeZoneIds.Value;

    /// <summary>Predefined cultures, sorted by their English name.</summary>
    public static IReadOnlyList<LocaleChoice> Locales => LocaleList.Value;

    /// <summary>The locale names of <see cref="Locales"/>.</summary>
    public static IEnumerable<string> LocaleNames => Locales.Select(choice => choice.Name);

    /// <summary>The time zones containing <paramref name="text"/> (any case); all when it is empty.</summary>
    public static IEnumerable<string> FindTimeZones(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? TimeZones
            : TimeZones.Where(zone => zone.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The locale names whose name or English name contains <paramref name="text"/> (any case); all when it is empty.</summary>
    public static IEnumerable<string> FindLocales(string? text) =>
        (string.IsNullOrWhiteSpace(text)
            ? Locales
            : Locales.Where(choice => choice.Name.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase)
                || choice.DisplayName.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase)))
        .Select(choice => choice.Name);

    /// <summary>The offered locale named <paramref name="name"/>, or <c>null</c>.</summary>
    public static LocaleChoice? FindLocale(string? name) => Locales.FirstOrDefault(choice => choice.Name == name);
}

/// <summary>One offered locale.</summary>
/// <param name="Name">The BCP 47 name, for example <c>pl-PL</c>.</param>
/// <param name="DisplayName">The English name, for example <c>Polish (Poland)</c>.</param>
public sealed record LocaleChoice(string Name, string DisplayName);
