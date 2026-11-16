using System.Globalization;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Persistence;
using FinTrack.Shared.Kernel.Results;
using FluentResults;

namespace FinTrack.Budget.Domain.Settings;

/// <summary>
/// The owner's settings (ARCHITECTURE 3, plan 1a D7): the currency totals are reported in, the time
/// zone that decides what "today" is, and the locale that formats numbers, dates and money. One
/// document per owner, stored in <c>budget_settings</c> and written through the ledger.
/// </summary>
/// <remarks>
/// <para>
/// Created with <see cref="Create"/> and changed with <see cref="Change"/>; both check the values and
/// return a new object, so an instance never holds an invalid value. The constructor is private:
/// the database mapping calls it to restore a stored document.
/// </para>
/// <para>
/// Values are accepted only in their exact, canonical form, never corrected: an IANA time zone ID
/// (<c>Europe/Warsaw</c>, not <c>europe/warsaw</c> and not a Windows ID) and the name of a predefined
/// culture (<c>pl-PL</c> or <c>pl</c>, not <c>pl-pl</c>). Which IDs and cultures exist comes from the
/// runtime's time zone and ICU data. Any registered currency can be the reporting currency; when
/// crypto currencies are registered (phase 3), this gains a rule limiting it to fiat.
/// </para>
/// </remarks>
public sealed class OwnerSettings : IMutableDocument
{
    /// <summary>The stored shape's version.</summary>
    public const int CurrentSchemaVersion = 1;

    // The mapping restores stored documents through this constructor; its parameter names match the properties.
    private OwnerSettings(
        Guid id,
        OwnerId ownerId,
        int schemaVersion,
        long version,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        Currency reportingCurrency,
        string timeZone,
        string locale)
    {
        Id = id;
        OwnerId = ownerId;
        SchemaVersion = schemaVersion;
        Version = version;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        ReportingCurrency = reportingCurrency;
        TimeZone = timeZone;
        Locale = locale;
    }

    /// <inheritdoc />
    public Guid Id { get; }

    /// <inheritdoc />
    public OwnerId OwnerId { get; }

    /// <inheritdoc />
    public int SchemaVersion { get; }

    /// <inheritdoc />
    public long Version { get; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; }

    /// <summary>The currency totals and reports are converted to.</summary>
    public Currency ReportingCurrency { get; }

    /// <summary>IANA time zone ID, for example <c>Europe/Warsaw</c>.</summary>
    public string TimeZone { get; }

    /// <summary>BCP 47 name of a predefined culture, for example <c>pl-PL</c>.</summary>
    public string Locale { get; }

    /// <summary>
    /// New settings for <paramref name="ownerId"/> (version 0, not stored yet). Fails with
    /// <see cref="BudgetErrorCodes.SettingsTimeZoneUnknown"/> and/or
    /// <see cref="BudgetErrorCodes.SettingsLocaleUnknown"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty (a bug).</exception>
    public static Result<OwnerSettings> Create(Guid id, OwnerId ownerId, Currency reportingCurrency, string timeZone, string locale)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Settings need an identifier.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(reportingCurrency);

        var check = Check(timeZone, locale);
        return check.IsFailed
            ? check.ToResult<OwnerSettings>()
            : Result.Ok(new OwnerSettings(id, ownerId, CurrentSchemaVersion, version: 0, default, default, reportingCurrency, timeZone, locale));
    }

    /// <summary>
    /// The same document with new values; identity, version and creation time are kept, so the
    /// ledger replaces the stored version. Fails like <see cref="Create"/>.
    /// </summary>
    public Result<OwnerSettings> Change(Currency reportingCurrency, string timeZone, string locale)
    {
        ArgumentNullException.ThrowIfNull(reportingCurrency);

        var check = Check(timeZone, locale);
        return check.IsFailed
            ? check.ToResult<OwnerSettings>()
            : Result.Ok(new OwnerSettings(Id, OwnerId, SchemaVersion, Version, CreatedAt, UpdatedAt, reportingCurrency, timeZone, locale));
    }

    /// <summary>Whether these settings already hold exactly these values (so saving them would change nothing).</summary>
    public bool HasValues(Currency reportingCurrency, string timeZone, string locale) =>
        ReportingCurrency == reportingCurrency
        && string.Equals(TimeZone, timeZone, StringComparison.Ordinal)
        && string.Equals(Locale, locale, StringComparison.Ordinal);

    private static Result Check(string? timeZone, string? locale)
    {
        var errors = new List<IError>();

        if (!IsIanaTimeZone(timeZone))
        {
            errors.Add(AppError.Validation(BudgetErrorCodes.SettingsTimeZoneUnknown));
        }

        if (!IsPredefinedLocale(locale))
        {
            errors.Add(AppError.Validation(BudgetErrorCodes.SettingsLocaleUnknown));
        }

        return errors.Count == 0 ? Result.Ok() : Result.Fail(errors);
    }

    // The lookup also finds other spellings (any case, Windows IDs on Windows); only the exact IANA ID passes.
    private static bool IsIanaTimeZone(string? timeZone) =>
        !string.IsNullOrEmpty(timeZone)
        && TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var found)
        && found.HasIanaId
        && string.Equals(found.Id, timeZone, StringComparison.Ordinal);

    // The lookup also normalises case and drops private-use parts; only the exact name passes.
    private static bool IsPredefinedLocale(string? locale)
    {
        if (string.IsNullOrEmpty(locale))
        {
            return false;
        }

        try
        {
            return string.Equals(CultureInfo.GetCultureInfo(locale, predefinedOnly: true).Name, locale, StringComparison.Ordinal);
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }
}
