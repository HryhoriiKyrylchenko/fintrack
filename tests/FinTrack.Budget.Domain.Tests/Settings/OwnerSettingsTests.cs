using FinTrack.Budget.Domain.Settings;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Domain.Tests.Settings;

/// <summary>
/// The owner's settings (plan 1a D7, ARCHITECTURE 3): a reporting currency, an IANA time zone and
/// a BCP 47 locale, each accepted only in its exact, known form.
/// </summary>
public class OwnerSettingsTests
{
    private static readonly Guid Id = Guid.Parse("0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b");
    private static readonly OwnerId Owner = OwnerId.From(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));

    [Fact]
    public void Create_WithKnownValues_IsANewDocument()
    {
        var settings = Create(Currency.Pln, "Europe/Warsaw", "pl-PL").Value;

        settings.Id.ShouldBe(Id);
        settings.OwnerId.ShouldBe(Owner);
        settings.SchemaVersion.ShouldBe(OwnerSettings.CurrentSchemaVersion);
        settings.Version.ShouldBe(0);
        settings.ReportingCurrency.ShouldBe(Currency.Pln);
        settings.TimeZone.ShouldBe("Europe/Warsaw");
        settings.Locale.ShouldBe("pl-PL");
    }

    [Theory]
    [InlineData("Europe/Warsaw")]
    [InlineData("America/New_York")]
    [InlineData("Etc/UTC")]
    public void Create_AcceptsIanaTimeZones(string timeZone) =>
        Create(Currency.Eur, timeZone, "en-US").IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData("europe/warsaw")]
    [InlineData("Central European Standard Time")]
    [InlineData("Mars/Olympus")]
    [InlineData(" Europe/Warsaw")]
    [InlineData("")]
    public void Create_WithATimeZoneThatIsNotAnExactIanaId_Fails(string timeZone) =>
        Create(Currency.Eur, timeZone, "en-US").ShouldFailWith(BudgetErrorCodes.SettingsTimeZoneUnknown, ErrorKind.Validation);

    [Theory]
    [InlineData("pl-PL")]
    [InlineData("pl")]
    [InlineData("en-US")]
    [InlineData("uk-UA")]
    public void Create_AcceptsPredefinedLocales(string locale) =>
        Create(Currency.Usd, "Europe/Warsaw", locale).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData("pl-pl")]
    [InlineData("xx-XX")]
    [InlineData("de-DE-x-test")]
    [InlineData("")]
    [InlineData("pl_PL")]
    public void Create_WithALocaleThatIsNotAnExactPredefinedName_Fails(string locale) =>
        Create(Currency.Usd, "Europe/Warsaw", locale).ShouldFailWith(BudgetErrorCodes.SettingsLocaleUnknown, ErrorKind.Validation);

    [Fact]
    public void Create_WithBothUnknown_ReportsBoth()
    {
        var result = Create(Currency.Usd, "Mars/Olympus", "xx-XX");

        result.Errors.Cast<AppError>().Select(error => error.Code)
            .ShouldBe([BudgetErrorCodes.SettingsTimeZoneUnknown, BudgetErrorCodes.SettingsLocaleUnknown]);
    }

    [Fact]
    public void Create_ErrorsCarryNoParameters() =>
        Create(Currency.Usd, "Mars/Olympus", "xx-XX").Errors.Cast<AppError>().ShouldAllBe(error => error.Parameters.Count == 0);

    [Fact]
    public void Create_WithAnEmptyId_IsABug() =>
        Should.Throw<ArgumentException>(() => OwnerSettings.Create(Guid.Empty, Owner, Currency.Pln, "Europe/Warsaw", "pl-PL"));

    [Fact]
    public void Change_KeepsIdentityAndVersion_AndTakesTheNewValues()
    {
        var stored = Stored();

        var changed = stored.Change(Currency.Eur, "America/New_York", "en-US").Value;

        changed.Id.ShouldBe(stored.Id);
        changed.OwnerId.ShouldBe(stored.OwnerId);
        changed.Version.ShouldBe(stored.Version);
        changed.CreatedAt.ShouldBe(stored.CreatedAt);
        changed.ReportingCurrency.ShouldBe(Currency.Eur);
        changed.TimeZone.ShouldBe("America/New_York");
        changed.Locale.ShouldBe("en-US");
    }

    [Fact]
    public void Change_LeavesTheOriginalUnchanged()
    {
        var stored = Stored();

        stored.Change(Currency.Eur, "America/New_York", "en-US");

        stored.ReportingCurrency.ShouldBe(Currency.Pln);
        stored.TimeZone.ShouldBe("Europe/Warsaw");
    }

    [Fact]
    public void Change_WithUnknownValues_Fails() =>
        Stored().Change(Currency.Eur, "Mars/Olympus", "en-US").ShouldFailWith(BudgetErrorCodes.SettingsTimeZoneUnknown, ErrorKind.Validation);

    [Theory]
    [InlineData("PLN", "Europe/Warsaw", "pl-PL", true)]
    [InlineData("EUR", "Europe/Warsaw", "pl-PL", false)]
    [InlineData("PLN", "Europe/Berlin", "pl-PL", false)]
    [InlineData("PLN", "Europe/Warsaw", "pl", false)]
    public void HasValues_ComparesAllThree(string currency, string timeZone, string locale, bool expected) =>
        Stored().HasValues(Currency.FromCode(currency).Value, timeZone, locale).ShouldBe(expected);

    [Theory]
    [InlineData("2026-01-15T23:30:00Z", "Europe/Warsaw", "2026-01-16")]
    [InlineData("2026-01-16T03:00:00Z", "America/New_York", "2026-01-15")]
    [InlineData("2026-07-15T22:00:00Z", "Europe/Warsaw", "2026-07-16")]
    public void Today_IsTheDateInTheOwnersTimeZone(string utcNow, string timeZone, string expected)
    {
        var settings = OwnerSettings.Create(Id, Owner, Currency.Pln, timeZone, "pl-PL").Value;

        settings.Today(DateTimeOffset.Parse(utcNow, System.Globalization.CultureInfo.InvariantCulture))
            .ShouldBe(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static FluentResults.Result<OwnerSettings> Create(Currency currency, string timeZone, string locale) =>
        OwnerSettings.Create(Id, Owner, currency, timeZone, locale);

    // As if read back from the database: the store stamps version and timestamps.
    private static OwnerSettings Stored() =>
        OwnerSettingsFactory.Restore(Id, Owner, version: 3, FakeClock.DefaultStart, FakeClock.DefaultStart.AddDays(1), Currency.Pln, "Europe/Warsaw", "pl-PL");
}
