using FinTrack.Budget.Contracts.V1.Settings;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Web.Settings;

namespace FinTrack.Web.Tests.Settings;

/// <summary>
/// The settings form (first run and Settings page): prefilled from the browser on first run, from
/// the stored settings afterwards, and turned into the Api's request.
/// </summary>
public class SettingsFormModelTests
{
    private static readonly string[] TimeZones = ["America/New_York", "Etc/UTC", "Europe/Berlin", "Europe/Warsaw"];
    private static readonly string[] Locales = ["de-DE", "en-GB", "en-US", "pl", "pl-PL"];

    [Fact]
    public void FirstRun_TakesTheBrowsersTimeZoneAndLocale()
    {
        var form = SettingsFormModel.ForFirstRun("Europe/Warsaw", "pl-PL", TimeZones, Locales);

        form.TimeZone.ShouldBe("Europe/Warsaw");
        form.Locale.ShouldBe("pl-PL");
    }

    [Theory]
    [InlineData("pl-PL", "PLN")]
    [InlineData("de-DE", "EUR")]
    [InlineData("en-US", "USD")]
    // A region with another currency, or no region at all: the first offered currency.
    [InlineData("en-GB", "PLN")]
    [InlineData("pl", "PLN")]
    public void FirstRun_PicksTheCurrencyOfTheLocalesRegion_WhenItIsOffered(string locale, string currency) =>
        SettingsFormModel.ForFirstRun("Europe/Warsaw", locale, TimeZones, Locales).ReportingCurrency.Code.ShouldBe(currency);

    [Fact]
    public void FirstRun_WithBrowserValuesThatAreNotOffered_FallsBackToUtcAndUsEnglish()
    {
        var form = SettingsFormModel.ForFirstRun("Mars/Olympus", "xx-XX", TimeZones, Locales);

        form.TimeZone.ShouldBe(SettingsFormModel.FallbackTimeZone);
        form.Locale.ShouldBe(SettingsFormModel.FallbackLocale);
        form.ReportingCurrency.ShouldBe(Currency.Usd);
    }

    [Fact]
    public void OfferedCurrencies_ArePlnEurUsd() =>
        SettingsFormModel.ReportingCurrencies.ShouldBe([Currency.Pln, Currency.Eur, Currency.Usd]);

    [Fact]
    public void FromSettings_ShowsTheStoredValues_AndHasNoChanges()
    {
        var stored = new OwnerSettingsDto(Currency.Eur, "Europe/Berlin", "de-DE");

        var form = SettingsFormModel.FromSettings(stored);

        form.ReportingCurrency.ShouldBe(Currency.Eur);
        form.TimeZone.ShouldBe("Europe/Berlin");
        form.Locale.ShouldBe("de-DE");
        form.HasChanges(stored).ShouldBeFalse();
        form.ChangesLocale(stored).ShouldBeFalse();
    }

    [Theory]
    [InlineData("USD", "Europe/Berlin", "de-DE", false)]
    [InlineData("EUR", "Europe/Warsaw", "de-DE", false)]
    [InlineData("EUR", "Europe/Berlin", "pl-PL", true)]
    public void AnyChangedValue_IsAChange_AndOnlyTheLocaleNeedsAReload(string currency, string timeZone, string locale, bool changesLocale)
    {
        var stored = new OwnerSettingsDto(Currency.Eur, "Europe/Berlin", "de-DE");
        var form = SettingsFormModel.FromSettings(stored);

        form.ReportingCurrency = Currency.FromCode(currency).Value;
        form.TimeZone = timeZone;
        form.Locale = locale;

        form.HasChanges(stored).ShouldBeTrue();
        form.ChangesLocale(stored).ShouldBe(changesLocale);
    }

    [Fact]
    public void CanSave_OnFirstRun_WhenComplete()
    {
        var form = SettingsFormModel.ForFirstRun("Europe/Warsaw", "pl-PL", TimeZones, Locales);

        form.CanSave(saved: null).ShouldBeTrue();

        form.Locale = string.Empty;
        form.CanSave(saved: null).ShouldBeFalse();
    }

    [Fact]
    public void CanSave_OnTheSettingsPage_OnlyAfterAChange()
    {
        var stored = new OwnerSettingsDto(Currency.Eur, "Europe/Berlin", "de-DE");
        var form = SettingsFormModel.FromSettings(stored);

        form.CanSave(stored).ShouldBeFalse();

        form.ReportingCurrency = Currency.Pln;
        form.CanSave(stored).ShouldBeTrue();

        form.TimeZone = string.Empty;
        form.CanSave(stored).ShouldBeFalse();
    }

    [Fact]
    public void ToRequest_CarriesTheValues()
    {
        var form = SettingsFormModel.ForFirstRun("Europe/Warsaw", "pl-PL", TimeZones, Locales);

        form.ToRequest().ShouldBe(new SaveOwnerSettingsRequest(Currency.Pln, "Europe/Warsaw", "pl-PL"));
    }

    [Theory]
    [InlineData("", "pl-PL", false)]
    [InlineData("Europe/Warsaw", "", false)]
    [InlineData("Europe/Warsaw", "pl-PL", true)]
    public void IsComplete_NeedsATimeZoneAndALocale(string timeZone, string locale, bool expected)
    {
        var form = SettingsFormModel.ForFirstRun("Europe/Warsaw", "pl-PL", TimeZones, Locales);
        form.TimeZone = timeZone;
        form.Locale = locale;

        form.IsComplete.ShouldBe(expected);
    }

    [Fact]
    public void Preview_FormatsTheSampleInTheChosenLocaleAndCurrency()
    {
        var form = SettingsFormModel.ForFirstRun("Europe/Warsaw", "pl-PL", TimeZones, Locales);

        var preview = form.Preview().ShouldNotBeNull();

        // Polish: space-like group separator, a decimal comma and the code after the number; day before month.
        preview.Amount.ShouldBe("1\u00A0234,56\u00A0PLN");
        preview.Date.ShouldBe("15.01.2026");

        form.Locale = "en-US";
        form.ReportingCurrency = Currency.Usd;
        form.Preview().ShouldBe(new LocalePreview("USD\u00A01,234.56", "1/15/2026"));
    }

    [Fact]
    public void Preview_OfAnUnknownLocale_IsEmpty()
    {
        var form = SettingsFormModel.ForFirstRun("Europe/Warsaw", "pl-PL", TimeZones, Locales);
        form.Locale = "xx-XX";

        form.Preview().ShouldBeNull();
    }
}
