using System.Net;
using System.Text;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Web.Api;

namespace FinTrack.Web.Tests.Api;

/// <summary>
/// Every answer of the Api becomes either a value (or plain success) or an error code; the UI never
/// sees server text (ARCHITECTURE 2.7). Bodies go both ways in the kernel's JSON contract.
/// </summary>
public class ApiClientTests
{
    private const string ProblemJson = "application/problem+json";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Success_ReturnsTheValue()
    {
        var client = Client(_ => Json(HttpStatusCode.OK, """{"status":"healthy","checkedAt":"2026-01-15T09:30:00.123Z"}"""));

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
    // The contract has one spelling of UTC; an offset is not accepted, even +00:00.
    [InlineData("""{"status":"healthy","checkedAt":"2026-01-15T09:30:00.123+00:00"}""")]
    [InlineData("""{"status":"healthy","checkedAt":"2026-01-15T09:30:00.123Z","extra":1}""")]
    [InlineData("""{"status":"healthy"}""")]
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

    // --- Calls that send a body (commands: POST and PUT, answered with 204) ---

    private static readonly PaymentProbe Payment = new(Money.From(1.10m, Currency.Pln), new DateOnly(2026, 1, 15));

    public static TheoryData<string> BodyMethods => ["POST", "PUT"];

    [Theory]
    [MemberData(nameof(BodyMethods))]
    public async Task BodyCall_SendsTheBodyInTheJsonContract(string method)
    {
        HttpRequestMessage? sent = null;
        string? body = null;
        var client = AsyncClient(async request =>
        {
            sent = request;
            body = await request.Content!.ReadAsStringAsync(Cancellation);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        await SendAsync(client, method, "v1/journal/deposits", Payment);

        sent.ShouldNotBeNull();
        sent.Method.Method.ShouldBe(method);
        sent.RequestUri.ShouldBe(new Uri("http://localhost:7071/api/v1/journal/deposits"));
        sent.Content!.Headers.ContentType!.MediaType.ShouldBe("application/json");
        body.ShouldBe("""{"amount":{"amount":"1.10","currency":"PLN"},"occurredOn":"2026-01-15"}""");
    }

    [Theory]
    [InlineData("POST", HttpStatusCode.NoContent, "")]
    [InlineData("PUT", HttpStatusCode.NoContent, "")]
    [InlineData("POST", HttpStatusCode.OK, "{}")]
    [InlineData("PUT", HttpStatusCode.Created, "not json")]
    public async Task BodyCall_AnySuccessStatus_IsSuccessWithoutAValue(string method, HttpStatusCode status, string answer)
    {
        var client = Client(_ => Json(status, answer));

        var result = await SendAsync(client, method, "v1/settings", Payment);

        result.IsSuccess.ShouldBeTrue();
        result.ErrorCode.ShouldBeNull();
        result.ErrorParams.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(BodyMethods))]
    public async Task BodyCall_Problem_ReturnsItsCodeAndParameters(string method)
    {
        var client = Client(_ => Json(
            HttpStatusCode.BadRequest,
            """{"status":400,"code":"validation.failed","params":{"field":"amount","rule":"JsonFormat"},"errors":[]}""",
            ProblemJson));

        var result = await SendAsync(client, method, "v1/settings", Payment);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorCode.ShouldBe("validation.failed");
        result.ErrorParams["field"].GetString().ShouldBe("amount");
        result.ErrorParams["rule"].GetString().ShouldBe("JsonFormat");
    }

    [Theory]
    [MemberData(nameof(BodyMethods))]
    public async Task BodyCall_ServerNotReachable_IsApiUnreachable(string method)
    {
        var client = Client(_ => throw new HttpRequestException("Connection refused (localhost:7071)"));

        var result = await SendAsync(client, method, "v1/settings", Payment);

        result.ErrorCode.ShouldBe(ApiClient.ApiUnreachable);
    }

    [Theory]
    [InlineData("POST", HttpStatusCode.InternalServerError, "<html>Server error</html>", "text/html")]
    [InlineData("PUT", HttpStatusCode.ServiceUnavailable, """{"status":503}""", ProblemJson)]
    [InlineData("POST", HttpStatusCode.BadRequest, """{"code":"validation.failed"}""", "application/json")]
    public async Task BodyCall_ErrorWithoutAUsableProblem_IsUnexpectedResponse(string method, HttpStatusCode status, string answer, string mediaType)
    {
        var client = Client(_ => Json(status, answer, mediaType));

        var result = await SendAsync(client, method, "v1/settings", Payment);

        result.ErrorCode.ShouldBe(ApiClient.UnexpectedResponse);
        result.ErrorParams.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(BodyMethods))]
    public async Task BodyCall_WithABodyTheContractCannotWrite_ThrowsBeforeSending(string method)
    {
        var sent = false;
        var client = Client(_ =>
        {
            sent = true;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        // An instant with an offset is a bug in the caller, not an Api error.
        await Should.ThrowAsync<InvalidOperationException>(() =>
            SendAsync(client, method, "v1/settings", new InstantProbe(new DateTimeOffset(2026, 1, 15, 9, 30, 0, TimeSpan.FromHours(1)))));

        sent.ShouldBeFalse();
    }

    private static Task<ApiResult> SendAsync<TBody>(ApiClient client, string method, string path, TBody body) =>
        method switch
        {
            "POST" => client.PostAsync(path, body, Cancellation),
            "PUT" => client.PutAsync(path, body, Cancellation),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "POST or PUT"),
        };

    private static ApiClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        AsyncClient(request => Task.FromResult(respond(request)));

    private static ApiClient AsyncClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) =>
        new(new HttpClient(new FakeHandler(respond)) { BaseAddress = new Uri("http://localhost:7071/api/") });

    private static HttpResponseMessage Json(HttpStatusCode status, string body, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    /// <summary>Answers every request without a network.</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }

    /// <summary>A command body with an amount and a business date.</summary>
    private sealed record PaymentProbe(Money Amount, DateOnly OccurredOn);

    private sealed record InstantProbe(DateTimeOffset At);
}
