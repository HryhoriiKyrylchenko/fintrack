using System.Globalization;
using FinTrack.Budget.Contracts.V1.Settings;
using FinTrack.Shared.Kernel.Monetary;

namespace FinTrack.Web.Settings;

/// <summary>
/// The values of the settings form, used on the first-run screen and the Settings page. Razor
/// components only bind to it; the rules live here, where they are tested.
/// </summary>
public sealed class SettingsFormModel
{
    /// <summary>Used when the browser's time zone is not offered.</summary>
    public const string FallbackTimeZone = "Etc/UTC";

    /// <summary>Used when the browser's language is not offered.</summary>
    public const string FallbackLocale = "en-US";

    // Invented sample values for the preview (CLAUDE.md rule 14).
    private const decimal SampleNumber = 1234.56m;
    private static readonly DateOnly SampleDate = new(2026, 1, 15);

    private SettingsFormModel(Currency reportingCurrency, string timeZone, string locale)
    {
        ReportingCurrency = reportingCurrency;
        TimeZone = timeZone;
        Locale = locale;
    }

    /// <summary>The currencies offered for reporting (plan 1a D7).</summary>
    public static IReadOnlyList<Currency> ReportingCurrencies { get; } = [Currency.Pln, Currency.Eur, Currency.Usd];

    /// <summary>The chosen reporting currency.</summary>
    public Currency ReportingCurrency { get; set; }

    /// <summary>The chosen IANA time zone ID.</summary>
    public string TimeZone { get; set; }

    /// <summary>The chosen locale name.</summary>
    public string Locale { get; set; }

    /// <summary>Whether a time zone and a locale are chosen.</summary>
    public bool IsComplete => !string.IsNullOrEmpty(TimeZone) && !string.IsNullOrEmpty(Locale);

    /// <summary>
    /// The form on first run: the browser's time zone and language when they are offered (otherwise
    /// <see cref="FallbackTimeZone"/> and <see cref="FallbackLocale"/>), and the currency of the
    /// locale's region when it is offered (otherwise the first offered currency).
    /// </summary>
    public static SettingsFormModel ForFirstRun(string? browserTimeZone, string? browserLocale, IEnumerable<string> timeZones, IEnumerable<string> locales)
    {
        ArgumentNullException.ThrowIfNull(timeZones);
        ArgumentNullException.ThrowIfNull(locales);

        var timeZone = browserTimeZone is not null && timeZones.Contains(browserTimeZone, StringComparer.Ordinal) ? browserTimeZone : FallbackTimeZone;
        var locale = browserLocale is not null && locales.Contains(browserLocale, StringComparer.Ordinal) ? browserLocale : FallbackLocale;

        return new SettingsFormModel(CurrencyOfRegion(locale) ?? ReportingCurrencies[0], timeZone, locale);
    }

    /// <summary>The form showing the saved settings.</summary>
    public static SettingsFormModel FromSettings(OwnerSettingsDto settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new SettingsFormModel(settings.ReportingCurrency, settings.TimeZone, settings.Locale);
    }

    /// <summary>
    /// Whether the form may be submitted: complete, and on the Settings page (<paramref name="saved"/>
    /// set) only after a change. On first run (<c>null</c>) it may always be submitted.
    /// </summary>
    public bool CanSave(OwnerSettingsDto? saved) => IsComplete && (saved is null || HasChanges(saved));

    /// <summary>Whether any value differs from <paramref name="saved"/>.</summary>
    public bool HasChanges(OwnerSettingsDto saved) => ToDto() != saved;

    /// <summary>Whether the locale differs from <paramref name="saved"/>; the app then reloads to format by it.</summary>
    public bool ChangesLocale(OwnerSettingsDto saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        return !string.Equals(Locale, saved.Locale, StringComparison.Ordinal);
    }

    /// <summary>The Api request for these values.</summary>
    public SaveOwnerSettingsRequest ToRequest() => new(ReportingCurrency, TimeZone, Locale);

    /// <summary>How a number and a date look in the chosen locale; <c>null</c> when the locale is not known here.</summary>
    public LocalePreview? Preview()
    {
        if (string.IsNullOrEmpty(Locale))
        {
            return null;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(Locale, predefinedOnly: true);
            return new LocalePreview(SampleNumber.ToString("N2", culture), SampleDate.ToString("d", culture));
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    private OwnerSettingsDto ToDto() => new(ReportingCurrency, TimeZone, Locale);

    // RegionInfo needs a specific culture ("pl-PL"); a neutral one ("pl") has no region.
    private static Currency? CurrencyOfRegion(string locale)
    {
        try
        {
            var code = new RegionInfo(locale).ISOCurrencySymbol;
            return ReportingCurrencies.FirstOrDefault(currency => currency.Code == code);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>A sample number and date, formatted in a locale.</summary>
/// <param name="Number">An invented amount with two decimals.</param>
/// <param name="Date">An invented date in the short date format.</param>
public sealed record LocalePreview(string Number, string Date);
