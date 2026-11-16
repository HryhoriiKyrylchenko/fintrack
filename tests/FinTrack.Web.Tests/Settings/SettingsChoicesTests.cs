using FinTrack.Web.Settings;

namespace FinTrack.Web.Tests.Settings;

/// <summary>The locales the form offers and how they are searched; and that an unknown locale is never applied.</summary>
public class SettingsChoicesTests
{
    [Fact]
    public void Locales_ContainPredefinedCultures_ButNotTheInvariantOne()
    {
        SettingsChoices.LocaleNames.ShouldContain("pl-PL");
        SettingsChoices.LocaleNames.ShouldContain("en-US");
        SettingsChoices.LocaleNames.ShouldNotContain(string.Empty);
    }

    [Theory]
    [InlineData("polish (pol", "pl-PL")]
    [InlineData("PL-pl", "pl-PL")]
    [InlineData("German (Germany)", "de-DE")]
    public void FindLocales_MatchesNameOrEnglishName_InAnyCase(string text, string expected) =>
        SettingsChoices.FindLocales(text).ShouldContain(expected);

    [Fact]
    public void FindLocales_WithoutText_ReturnsAll() =>
        SettingsChoices.FindLocales(" ").Count().ShouldBe(SettingsChoices.Locales.Count);

    [Fact]
    public void FindLocale_KnowsTheEnglishName()
    {
        SettingsChoices.FindLocale("pl-PL")!.DisplayName.ShouldBe("Polish (Poland)");
        SettingsChoices.FindLocale("xx-XX").ShouldBeNull();
    }

    [Fact]
    public void UnknownLocale_IsNotApplied()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;

        OwnerCulture.TryApply("xx-XX").ShouldBeFalse();

        System.Globalization.CultureInfo.CurrentCulture.ShouldBe(before);
    }
}
