using FinTrack.Web.Api;
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
        "settings.time_zone_unknown",
        "settings.locale_unknown",
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
    public void UnknownCode_GetsTheGeneralText()
    {
        var text = SharedResource.ErrorText(Localizer, "bucket.some_future_rule");

        text.ShouldBe(Localizer["Error.unknown"].Value);
        text.ShouldNotContain("bucket");
    }

    [Fact]
    public void ApiUnreachable_HasTheExpectedText() =>
        SharedResource.ErrorText(Localizer, ApiClient.ApiUnreachable).ShouldBe("The server cannot be reached. Start it, then check again.");

    public void Dispose() => services.Dispose();
}
