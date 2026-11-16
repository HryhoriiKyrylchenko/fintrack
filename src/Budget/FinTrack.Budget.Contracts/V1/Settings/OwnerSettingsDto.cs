using FinTrack.Shared.Kernel.Monetary;

namespace FinTrack.Budget.Contracts.V1.Settings;

/// <summary>The owner's settings, version 1 of the contract.</summary>
/// <param name="ReportingCurrency">The currency totals and reports are converted to; JSON: its code, for example <c>"PLN"</c>.</param>
/// <param name="TimeZone">IANA time zone ID that decides what "today" is, for example <c>"Europe/Warsaw"</c>.</param>
/// <param name="Locale">BCP 47 name of the culture that formats numbers, dates and money, for example <c>"pl-PL"</c>.</param>
public sealed record OwnerSettingsDto(Currency ReportingCurrency, string TimeZone, string Locale);
