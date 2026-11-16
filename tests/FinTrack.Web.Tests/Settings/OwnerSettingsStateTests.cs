using System.Net;
using System.Text;
using FinTrack.Budget.Contracts.V1.Settings;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Web.Api;
using FinTrack.Web.Settings;

namespace FinTrack.Web.Tests.Settings;

/// <summary>
/// What the app knows about the owner's settings at start: saved (the app runs with them), not
/// saved yet (first run: only the setup screen), or unknown because the Api failed (an error with retry).
/// </summary>
public class OwnerSettingsStateTests
{
    private const string ProblemJson = "application/problem+json";
    private const string SettingsJson = """{"reportingCurrency":"EUR","timeZone":"Europe/Berlin","locale":"de-DE"}""";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void BeforeLoading_IsLoading() =>
        new OwnerSettingsState(Client(_ => Json(HttpStatusCode.OK, SettingsJson))).Status.ShouldBe(SettingsStatus.Loading);

    [Fact]
    public async Task SavedSettings_AreLoaded()
    {
        var state = new OwnerSettingsState(Client(_ => Json(HttpStatusCode.OK, SettingsJson)));

        await state.LoadAsync(Cancellation);

        state.Status.ShouldBe(SettingsStatus.Saved);
        state.Settings.ShouldBe(new OwnerSettingsDto(Currency.Eur, "Europe/Berlin", "de-DE"));
        state.ErrorCode.ShouldBeNull();
    }

    [Fact]
    public async Task SettingsNotFound_IsTheFirstRun()
    {
        var state = new OwnerSettingsState(Client(_ => Json(HttpStatusCode.NotFound, """{"status":404,"code":"settings.not_found"}""", ProblemJson)));

        await state.LoadAsync(Cancellation);

        state.Status.ShouldBe(SettingsStatus.NotSaved);
        state.Settings.ShouldBeNull();
        state.ErrorCode.ShouldBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"status":503,"code":"health.database_unavailable"}""", ProblemJson, "health.database_unavailable")]
    [InlineData(HttpStatusCode.InternalServerError, "oops", "text/plain", ApiClient.UnexpectedResponse)]
    // Another 404 (for example a wrong route) is not a first run.
    [InlineData(HttpStatusCode.NotFound, "", "text/plain", ApiClient.UnexpectedResponse)]
    public async Task AnyOtherFailure_IsFailed_WithItsCode(HttpStatusCode status, string body, string mediaType, string code)
    {
        var state = new OwnerSettingsState(Client(_ => Json(status, body, mediaType)));

        await state.LoadAsync(Cancellation);

        state.Status.ShouldBe(SettingsStatus.Failed);
        state.ErrorCode.ShouldBe(code);
        state.Settings.ShouldBeNull();
    }

    [Fact]
    public async Task ServerNotReachable_IsFailed()
    {
        var state = new OwnerSettingsState(Client(_ => throw new HttpRequestException("refused")));

        await state.LoadAsync(Cancellation);

        state.Status.ShouldBe(SettingsStatus.Failed);
        state.ErrorCode.ShouldBe(ApiClient.ApiUnreachable);
    }

    [Fact]
    public async Task Save_PutsTheRequest_AndKeepsTheSavedValues()
    {
        string? sent = null;
        var state = new OwnerSettingsState(Client(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Json(HttpStatusCode.NotFound, """{"code":"settings.not_found"}""", ProblemJson);
            }

            request.RequestUri!.AbsolutePath.ShouldBe("/api/v1/settings");
            sent = request.Content!.ReadAsStringAsync(Cancellation).GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        await state.LoadAsync(Cancellation);
        var request = new SaveOwnerSettingsRequest(Currency.Pln, "Europe/Warsaw", "pl-PL");

        var result = await state.SaveAsync(request, Cancellation);

        result.IsSuccess.ShouldBeTrue();
        sent.ShouldBe("""{"reportingCurrency":"PLN","timeZone":"Europe/Warsaw","locale":"pl-PL"}""");
        state.Status.ShouldBe(SettingsStatus.Saved);
        state.Settings.ShouldBe(new OwnerSettingsDto(Currency.Pln, "Europe/Warsaw", "pl-PL"));
    }

    [Fact]
    public async Task FailedSave_ReturnsTheCode_AndChangesNothing()
    {
        var state = new OwnerSettingsState(Client(request => request.Method == HttpMethod.Get
            ? Json(HttpStatusCode.OK, SettingsJson)
            : Json(HttpStatusCode.BadRequest, """{"code":"settings.time_zone_unknown"}""", ProblemJson)));
        await state.LoadAsync(Cancellation);

        var result = await state.SaveAsync(new SaveOwnerSettingsRequest(Currency.Pln, "Mars/Olympus", "pl-PL"), Cancellation);

        result.ErrorCode.ShouldBe("settings.time_zone_unknown");
        state.Settings.ShouldBe(new OwnerSettingsDto(Currency.Eur, "Europe/Berlin", "de-DE"));
    }

    [Fact]
    public async Task EveryChange_IsAnnounced()
    {
        var state = new OwnerSettingsState(Client(_ => Json(HttpStatusCode.OK, SettingsJson)));
        var announced = 0;
        state.Changed += () => announced++;

        await state.LoadAsync(Cancellation);

        announced.ShouldBe(1);
    }

    private static ApiClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new FakeHandler(respond)) { BaseAddress = new Uri("http://localhost:7071/api/") });

    private static HttpResponseMessage Json(HttpStatusCode status, string body, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
