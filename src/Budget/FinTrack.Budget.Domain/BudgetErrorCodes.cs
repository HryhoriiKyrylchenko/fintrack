namespace FinTrack.Budget.Domain;

/// <summary>
/// Error codes of the Budget context. A public contract: the UI maps each one to a text in its
/// resource files. Never rename or reuse a code; add a new one instead.
/// </summary>
public static class BudgetErrorCodes
{
    /// <summary>The owner has not saved settings yet (first run). No parameters.</summary>
    public const string SettingsNotFound = "settings.not_found";

    /// <summary>The time zone is not an IANA time zone ID in its exact form, for example <c>Europe/Warsaw</c>. No parameters.</summary>
    public const string SettingsTimeZoneUnknown = "settings.time_zone_unknown";

    /// <summary>The locale is not the exact name of a predefined culture, for example <c>pl-PL</c>. No parameters.</summary>
    public const string SettingsLocaleUnknown = "settings.locale_unknown";
}
