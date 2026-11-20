using System.Globalization;
using System.Reflection;
using System.Text.Json;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Web.Api;
using FinTrack.Web.Formatting;
using FinTrack.Web.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinTrack.Web.Tests.Resources;

/// <summary>Error codes become text from the resource files (DESIGN 7); a code without text never shows as a raw code.</summary>
public sealed class ErrorTextTests : IDisposable
{
    // The WebAssembly host provides logging; here the null logger stands in for it.
    private readonly ServiceProvider services = new ServiceCollection()
        .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
        .AddLocalization()
        .BuildServiceProvider();

    private IStringLocalizer<SharedResource> Localizer => services.GetRequiredService<IStringLocalizer<SharedResource>>();

    /// <summary>Every code the Web app can receive or produce today. A new code needs a text here and in the resources.</summary>
    public static TheoryData<string> KnownCodes =>
    [
        ApiClient.ApiUnreachable,
        ApiClient.UnexpectedResponse,
        "server.error",
        "health.database_unavailable",
        "settings.not_found",
        "validation.failed",
        "id.invalid",
        "bucket.not_found",
        "bucket.archived",
        "bucket.name_taken",
        "bucket.id_reused",
        "bucket.system_bucket",
        "bucket.currency_mismatch",
        "money.amount_not_positive",
        "entry.id_reused",
        "entry.date_in_future",
        "bucket.insufficient_funds",
        "bucket.spend_not_allowed",
        "bucket.opening_balance_exists",
        "transfer.same_bucket",
        "transfer.fee_exceeds_amount",
        "transfer.amounts_inconsistent",
        "bucket.not_empty",
        "entry.not_found",
        "entry.already_reversed",
        "entry.not_reversible",
        "entry.has_dependents",
        "settings.time_zone_unknown",
        "settings.locale_unknown",
        "fx.rate_unavailable",
        "money.too_many_decimals",
        "input.amount_invalid",
        "input.amount_required",
        "concurrency.conflict",
        "input.target_required",
        "input.note_invalid",
        "input.tag_invalid",
        "input.tag_duplicate",
        "input.tags_too_many",
        "input.rate_required",
        "input.rate_invalid",
        "input.period_invalid",
        "currency.unknown",
    ];

    [Theory]
    [MemberData(nameof(KnownCodes))]
    public void KnownCode_HasItsOwnText(string code)
    {
        var text = SharedResource.ErrorText(Localizer, code);

        text.ShouldNotBe(Localizer["Error.unknown"].Value);
        text.ShouldNotContain(code);
        text.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void EveryKernelErrorCode_HasItsOwnText()
    {
        var codes = typeof(ErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        codes.ShouldNotBeEmpty();
        codes.ShouldAllBe(code => SharedResource.ErrorText(Localizer, code) != Localizer["Error.unknown"].Value);
    }

    [Fact]
    public void UnknownCode_GetsTheGeneralText()
    {
        var text = SharedResource.ErrorText(Localizer, "bucket.some_future_rule");

        text.ShouldBe(Localizer["Error.unknown"].Value);
        text.ShouldNotContain("bucket");
    }

    [Fact]
    public void ApiUnreachable_HasTheExpectedText() =>
        SharedResource.ErrorText(Localizer, ApiClient.ApiUnreachable).ShouldBe("The server cannot be reached. Start it, then check again.");

    [Fact]
    public void Parameters_FillTheTemplate_AndADateIsFormattedInTheLocale()
    {
        var error = new ErrorInfo("bucket.insufficient_funds", new Dictionary<string, string> { ["date"] = "2026-01-15" });

        SharedResource.ErrorText(Localizer, error, CultureInfo.GetCultureInfo("pl-PL"))
            .ShouldBe("This would make the bucket's balance negative on 15.01.2026. Check the amount and the date, or add the money first.");
        SharedResource.ErrorText(Localizer, error, CultureInfo.GetCultureInfo("en-US")).ShouldContain("negative on 1/15/2026.");
    }

    [Fact]
    public void MissingParameter_GivesThePlainText()
    {
        var error = new ErrorInfo("bucket.insufficient_funds", new Dictionary<string, string>());

        SharedResource.ErrorText(Localizer, error, CultureInfo.GetCultureInfo("en-US")).ShouldBe(SharedResource.ErrorText(Localizer, "bucket.insufficient_funds"));
    }

    [Fact]
    public void CodeWithoutATemplate_IgnoresItsParameters()
    {
        var error = new ErrorInfo("validation.failed", new Dictionary<string, string> { ["field"] = "amount", ["rule"] = "NotNullValidator" });

        SharedResource.ErrorText(Localizer, error, CultureInfo.GetCultureInfo("en-US")).ShouldBe(SharedResource.ErrorText(Localizer, "validation.failed"));
    }

    [Fact]
    public void TooManyDecimals_NamesTheCurrencyAndItsPrecision()
    {
        var error = ErrorInfo.From(MoneyInput.Parse("1.234", Currency.Pln, CultureInfo.GetCultureInfo("en-US")).Errors[0]);

        SharedResource.ErrorText(Localizer, error, CultureInfo.GetCultureInfo("en-US")).ShouldBe("PLN amounts have at most 2 decimal places.");
    }

    [Fact]
    public void InvalidAmount_ShowsAnExampleInTheLocale()
    {
        var error = ErrorInfo.From(MoneyInput.Parse("12.50", Currency.Eur, CultureInfo.GetCultureInfo("de-DE")).Errors[0]);

        SharedResource.ErrorText(Localizer, error, CultureInfo.GetCultureInfo("de-DE")).ShouldBe("Enter the amount as a number, for example 1.234,56.");
    }

    [Fact]
    public void ApiParameters_AreReadAsText()
    {
        using var json = JsonDocument.Parse("""{"date":"2026-01-15","maxDecimalPlaces":2,"strict":true}""");
        var parameters = json.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());

        var error = ErrorInfo.FromApi("bucket.insufficient_funds", parameters);

        error.Parameters.ShouldBe(new Dictionary<string, string> { ["date"] = "2026-01-15", ["maxDecimalPlaces"] = "2", ["strict"] = "true" }, ignoreOrder: true);
    }

    [Fact]
    public void AnErrorThatIsNotAnAppError_IsTheGeneralError() =>
        ErrorInfo.From(new FluentResults.Error("boom")).Code.ShouldBe("client.unexpected_response");

    public void Dispose() => services.Dispose();
}
