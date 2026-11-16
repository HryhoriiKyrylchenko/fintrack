using System.Text;
using FinTrack.Api.Http;
using FinTrack.Api.IntegrationTests.Middleware;
using FinTrack.Api.Middleware;
using FinTrack.Budget.Contracts.V1.Settings;
using FinTrack.Shared.Infrastructure.Health;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FluentResults;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinTrack.Api.IntegrationTests.Http;

/// <summary>
/// What the client receives, byte for byte. The host is built exactly as <c>Program.cs</c> builds
/// it (<see cref="ApiComposition.ConfigureFinTrackApi"/>), and results are executed the way the
/// Functions ASP.NET Core integration executes them (<see cref="IActionResult.ExecuteResultAsync"/>,
/// which writes through MVC's JSON options).
/// </summary>
public sealed class JsonResponseTests : IDisposable
{
    private static readonly DateTimeOffset Instant = new(2026, 1, 15, 9, 30, 0, 123, TimeSpan.Zero);

    private readonly IHost host;

    public JsonResponseTests(ApiFixture fixture)
    {
        var builder = FunctionsApplication.CreateBuilder([]);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mongo:ConnectionString"] = fixture.ConnectionString,
            ["Mongo:DatabaseName"] = fixture.DatabaseName,
            ["FinTrack:LocalOwnerId"] = ApiFixture.LocalOwnerId,
        });
        builder.ConfigureFinTrackApi();
        host = builder.Build();
    }

    [Fact]
    public async Task Problem_IsWrittenExactlyAsBefore()
    {
        var error = AppError.BusinessRule("bucket.insufficient_funds", ("currency", "PLN"), ("maxDecimalPlaces", 2));

        var response = await ExecuteAsync(ProblemDetailsMapper.ToActionResult(Result.Fail(error)));

        response.Status.ShouldBe(422);
        response.ContentType.ShouldBe("application/problem+json; charset=utf-8");
        response.Body.ShouldBe(
            """{"type":"urn:fintrack:error:bucket.insufficient_funds","title":"bucket.insufficient_funds","status":422,"code":"bucket.insufficient_funds","params":{"currency":"PLN","maxDecimalPlaces":2},"errors":[{"code":"bucket.insufficient_funds","params":{"currency":"PLN","maxDecimalPlaces":2}}]}""");
    }

    [Fact]
    public async Task UnexpectedError_IsWrittenExactlyAsBefore()
    {
        var response = await ExecuteAsync(ProblemDetailsMapper.ToObjectResult(ProblemDetailsMapper.UnexpectedError()));

        response.Status.ShouldBe(500);
        response.Body.ShouldBe(UnexpectedErrorJson);
    }

    [Fact]
    public async Task ExceptionMiddleware_WritesTheSameUnexpectedError()
    {
        await using var scope = host.Services.CreateAsyncScope();
        var http = NewHttpContext(scope.ServiceProvider, out var body);
        var context = new FakeFunctionContext("AnyHttpFunction", "httpTrigger", scope.ServiceProvider);

        // The key under which the ASP.NET Core integration stores the request's HttpContext.
        context.Items["HttpRequestContext"] = http;

        await new ExceptionMiddleware(NullLogger<ExceptionMiddleware>.Instance)
            .Invoke(context, _ => throw new InvalidOperationException("a bug"));

        http.Response.StatusCode.ShouldBe(500);
        http.Response.ContentType.ShouldBe("application/problem+json");
        Encoding.UTF8.GetString(body.ToArray()).ShouldBe(UnexpectedErrorJson);
    }

    [Fact]
    public async Task Value_FollowsTheJsonContract()
    {
        var value = new ResponseProbe(Money.From(1.10m, Currency.Pln), 4.2500m, new DateOnly(2026, 1, 15), Instant);

        var response = await ExecuteAsync(ProblemDetailsMapper.ToActionResult(Result.Ok(value)));

        response.Status.ShouldBe(200);
        response.ContentType.ShouldBe("application/json; charset=utf-8");
        response.Body.ShouldBe("""{"price":{"amount":"1.10","currency":"PLN"},"rate":"4.2500","occurredOn":"2026-01-15","recordedAt":"2026-01-15T09:30:00.123Z"}""");
    }

    [Fact]
    public async Task HealthReport_HasItsInstantInUtc()
    {
        var response = await ExecuteAsync(ProblemDetailsMapper.ToActionResult(Result.Ok(new HealthReport(GetHealthHandler.Healthy, Instant))));

        response.Body.ShouldBe("""{"status":"healthy","checkedAt":"2026-01-15T09:30:00.123Z"}""");
    }

    [Fact]
    public async Task OwnerSettings_HaveTheV1Shape()
    {
        var response = await ExecuteAsync(ProblemDetailsMapper.ToActionResult(Result.Ok(new OwnerSettingsDto(Currency.Pln, "Europe/Warsaw", "pl-PL"))));

        response.Body.ShouldBe("""{"reportingCurrency":"PLN","timeZone":"Europe/Warsaw","locale":"pl-PL"}""");
    }

    public void Dispose() => host.Dispose();

    private const string UnexpectedErrorJson =
        """{"type":"urn:fintrack:error:server.error","title":"server.error","status":500,"code":"server.error","params":{},"errors":[]}""";

    private async Task<(int Status, string? ContentType, string Body)> ExecuteAsync(IActionResult result)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var http = NewHttpContext(scope.ServiceProvider, out var body);

        await result.ExecuteResultAsync(new ActionContext(http, new RouteData(), new ActionDescriptor()));

        return (http.Response.StatusCode, http.Response.ContentType, Encoding.UTF8.GetString(body.ToArray()));
    }

    private static DefaultHttpContext NewHttpContext(IServiceProvider services, out MemoryStream body)
    {
        body = new MemoryStream();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Response.Body = body;
        return http;
    }
}

/// <summary>A response with every kind of value the contract formats.</summary>
public sealed record ResponseProbe(Money Price, decimal Rate, DateOnly OccurredOn, DateTimeOffset RecordedAt);
