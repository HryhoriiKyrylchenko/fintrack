using System.Net;
using System.Text;
using FinTrack.Web.Api;

namespace FinTrack.Web.Tests.Api;

/// <summary>
/// Every answer of the Api becomes either a value or an error code; the UI never sees server text
/// (ARCHITECTURE 2.7).
/// </summary>
public class ApiClientTests
{
    private const string ProblemJson = "application/problem+json";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Success_ReturnsTheValue()
    {
        var client = Client(_ => Json(HttpStatusCode.OK, """{"status":"healthy","checkedAt":"2026-01-15T09:30:00.123+00:00"}"""));

        var result = await client.GetAsync<HealthStatus>("v1/health", Cancellation);

        result.IsSuccess.ShouldBeTrue();
        result.ErrorCode.ShouldBeNull();
        result.Value.ShouldBe(new HealthStatus("healthy", new DateTimeOffset(2026, 1, 15, 9, 30, 0, 123, TimeSpan.Zero)));
    }

    [Fact]
    public async Task Request_IsRelativeToTheApiBaseAddress()
    {
        Uri? requested = null;
        var client = Client(request =>
        {
            requested = request.RequestUri;
            return Json(HttpStatusCode.OK, """{"status":"healthy","checkedAt":"2026-01-15T09:30:00Z"}""");
        });

        await client.GetAsync<HealthStatus>("v1/health", Cancellation);

        requested.ShouldBe(new Uri("http://localhost:7071/api/v1/health"));
    }

    [Fact]
    public async Task Problem_ReturnsItsCodeAndParameters()
    {
        var client = Client(_ => Json(
            HttpStatusCode.UnprocessableEntity,
            """{"type":"urn:fintrack:error:bucket.insufficient_funds","title":"bucket.insufficient_funds","status":422,"code":"bucket.insufficient_funds","params":{"currency":"PLN","maxDecimalPlaces":2},"errors":[]}""",
            ProblemJson));

        var result = await client.GetAsync<HealthStatus>("v1/health", Cancellation);

        result.IsSuccess.ShouldBeFalse();
        result.Value.ShouldBeNull();
        result.ErrorCode.ShouldBe("bucket.insufficient_funds");
        result.ErrorParams["currency"].GetString().ShouldBe("PLN");
        result.ErrorParams["maxDecimalPlaces"].GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task ProblemWithoutParameters_HasAnEmptyParameterSet()
    {
        var client = Client(_ => Json(HttpStatusCode.ServiceUnavailable, """{"status":503,"code":"health.database_unavailable"}""", ProblemJson));

        var result = await client.GetAsync<HealthStatus>("v1/health", Cancellation);

        result.ErrorCode.ShouldBe("health.database_unavailable");
        result.ErrorParams.ShouldBeEmpty();
    }

    [Fact]
    public async Task ServerNotReachable_IsApiUnreachable()
    {
        var client = Client(_ => throw new HttpRequestException("Connection refused (localhost:7071)"));

        var result = await client.GetAsync<HealthStatus>("v1/health", Cancellation);

        result.ErrorCode.ShouldBe(ApiClient.ApiUnreachable);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "<html>Server error</html>", "text/html")]
    [InlineData(HttpStatusCode.BadGateway, "", "text/plain")]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"status":503}""", ProblemJson)]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"status":503,"code":""}""", ProblemJson)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "not json", ProblemJson)]
    [InlineData(HttpStatusCode.BadRequest, """{"code":"validation.failed"}""", "application/json")]
    public async Task ErrorWithoutAUsableProblem_IsUnexpectedResponse(HttpStatusCode status, string body, string mediaType)
    {
        var client = Client(_ => Json(status, body, mediaType));

        var result = await client.GetAsync<HealthStatus>("v1/health", Cancellation);

        result.ErrorCode.ShouldBe(ApiClient.UnexpectedResponse);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{"status":"healthy","checkedAt":"yesterday"}""")]
    public async Task SuccessWithoutAUsableValue_IsUnexpectedResponse(string body)
    {
        var client = Client(_ => Json(HttpStatusCode.OK, body));

        var result = await client.GetAsync<HealthStatus>("v1/health", Cancellation);

        result.ErrorCode.ShouldBe(ApiClient.UnexpectedResponse);
    }

    [Fact]
    public async Task ServerText_NeverBecomesTheErrorCode()
    {
        var client = Client(_ => Json(HttpStatusCode.InternalServerError, "Database password is wrong", "text/plain"));

        var result = await client.GetAsync<HealthStatus>("v1/health", Cancellation);

        result.ErrorCode.ShouldBe(ApiClient.UnexpectedResponse);
        result.ErrorParams.ShouldBeEmpty();
    }

    private static ApiClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new FakeHandler(respond)) { BaseAddress = new Uri("http://localhost:7071/api/") });

    private static HttpResponseMessage Json(HttpStatusCode status, string body, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    /// <summary>Answers every request without a network.</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
