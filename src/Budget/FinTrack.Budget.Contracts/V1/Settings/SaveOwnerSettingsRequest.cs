using FinTrack.Shared.Kernel.Monetary;

namespace FinTrack.Budget.Contracts.V1.Settings;

/// <summary>
/// Body of <c>PUT /api/v1/settings</c>: all settings at once. Answers 204. Saving the values that
/// are already stored changes nothing. An unknown time zone or locale fails with
/// <c>settings.time_zone_unknown</c> or <c>settings.locale_unknown</c> (400).
/// </summary>
/// <param name="ReportingCurrency">A registered currency code, for example <c>"PLN"</c>.</param>
/// <param name="TimeZone">An IANA time zone ID in its exact form, for example <c>"Europe/Warsaw"</c>.</param>
/// <param name="Locale">The exact name of a predefined culture, for example <c>"pl-PL"</c>.</param>
public sealed record SaveOwnerSettingsRequest(Currency ReportingCurrency, string TimeZone, string Locale);
