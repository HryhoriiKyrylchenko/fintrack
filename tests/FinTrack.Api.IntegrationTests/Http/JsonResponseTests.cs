using System.Text;
using FinTrack.Api.Http;
using FinTrack.Api.IntegrationTests.Middleware;
using FinTrack.Api.Middleware;
using FinTrack.Budget.Contracts.V1.Buckets;
using FinTrack.Budget.Contracts.V1.Journal;
using FinTrack.Budget.Contracts.V1.Overview;
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

    [Fact]
    public async Task Buckets_HaveTheV1Shape_WithEmptyValuesAsNull()
    {
        var id = Guid.Parse("0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b");
        IReadOnlyList<BucketDto> buckets =
        [
            new(id, "Crypto", Currency.Usd, BucketKind.Investment, InvestmentProfile.Crypto, BucketStatus.Active, BucketSystemRole.None),
            new(id, null, Currency.Pln, BucketKind.Savings, null, BucketStatus.Active, BucketSystemRole.Unallocated),
        ];

        var response = await ExecuteAsync(ProblemDetailsMapper.ToActionResult(Result.Ok(buckets)));

        response.Body.ShouldBe(
            "[{\"id\":\"0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b\",\"name\":\"Crypto\",\"currency\":\"USD\",\"kind\":\"Investment\",\"profile\":\"Crypto\",\"status\":\"Active\",\"systemRole\":\"None\"},"
            + "{\"id\":\"0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b\",\"name\":null,\"currency\":\"PLN\",\"kind\":\"Savings\",\"profile\":null,\"status\":\"Active\",\"systemRole\":\"Unallocated\"}]");
    }

    [Fact]
    public async Task Overview_HasTheV1Shape()
    {
        var id = Guid.Parse("0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b");
        var overview = new BudgetOverviewDto(
            Currency.Pln,
            new DateOnly(2026, 1, 15),
            [
                new CurrencyTotalDto(Currency.Pln, Money.From(10.50m, Currency.Pln), null),
                new CurrencyTotalDto(Currency.Eur, Money.From(2.00m, Currency.Eur), new ConversionDto(Money.From(8.55m, Currency.Pln), 4.2735m, new DateOnly(2026, 1, 14))),
            ],
            new ReportingTotalDto(Money.From(19.05m, Currency.Pln), new DateOnly(2026, 1, 14), null),
            [new BucketSummaryDto(id, null, Currency.Pln, BucketKind.Savings, null, BucketSystemRole.Unallocated, Money.From(10.50m, Currency.Pln), Money.From(10.50m, Currency.Pln), Money.From(0.00m, Currency.Pln))]);

        var response = await ExecuteAsync(ProblemDetailsMapper.ToActionResult(Result.Ok(overview)));

        response.Body.ShouldBe(
            "{\"reportingCurrency\":\"PLN\",\"asOf\":\"2026-01-15\",\"totals\":["
            + "{\"currency\":\"PLN\",\"available\":{\"amount\":\"10.50\",\"currency\":\"PLN\"},\"conversion\":null},"
            + "{\"currency\":\"EUR\",\"available\":{\"amount\":\"2.00\",\"currency\":\"EUR\"},\"conversion\":{\"amount\":{\"amount\":\"8.55\",\"currency\":\"PLN\"},\"rate\":\"4.2735\",\"rateDate\":\"2026-01-14\"}}],"
            + "\"reportingTotal\":{\"amount\":{\"amount\":\"19.05\",\"currency\":\"PLN\"},\"rateDate\":\"2026-01-14\",\"unavailableReason\":null},"
            + "\"buckets\":[{\"id\":\"0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b\",\"name\":null,\"currency\":\"PLN\",\"kind\":\"Savings\",\"profile\":null,\"systemRole\":\"Unallocated\","
            + "\"available\":{\"amount\":\"10.50\",\"currency\":\"PLN\"},\"netContributed\":{\"amount\":\"10.50\",\"currency\":\"PLN\"},\"outstandingPrincipal\":{\"amount\":\"0.00\",\"currency\":\"PLN\"}}]}");
    }

    [Fact]
    public async Task OverviewWithoutItsTotal_HasTheReasonInstead()
    {
        var overview = new BudgetOverviewDto(Currency.Pln, new DateOnly(2026, 1, 15), [], new ReportingTotalDto(null, null, "fx.rate_unavailable"), []);

        var response = await ExecuteAsync(ProblemDetailsMapper.ToActionResult(Result.Ok(overview)));

        response.Body.ShouldBe(
            """{"reportingCurrency":"PLN","asOf":"2026-01-15","totals":[],"reportingTotal":{"amount":null,"rateDate":null,"unavailableReason":"fx.rate_unavailable"},"buckets":[]}""");
    }

    [Fact]
    public async Task JournalPage_HasTheV1Shape()
    {
        var transfer = Guid.Parse("0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b");
        var reversal = Guid.Parse("0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2c");
        var from = Guid.Parse("0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2d");
        var to = Guid.Parse("0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2e");
        var page = new JournalPageDto(
            [
                new JournalEntryDto(reversal, JournalEntryType.Reversal, new DateOnly(2026, 1, 15), Instant, "Typo", [], null, "Manual", null,
                    null, null, null, null, null, null, null, null, transfer, null),
                new JournalEntryDto(transfer, JournalEntryType.Transfer, new DateOnly(2026, 1, 14), Instant, null, ["move"], null, "Manual", null,
                    null, null, from, to, Money.From(43.50m, Currency.Pln), Money.From(10.00m, Currency.Eur), Money.From(0.50m, Currency.Pln), 0.232558139535m, null, reversal),
            ],
            "AQGQ");

        var response = await ExecuteAsync(ProblemDetailsMapper.ToActionResult(Result.Ok(page)));

        response.Body.ShouldBe(
            "{\"entries\":["
            + "{\"id\":\"0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2c\",\"type\":\"Reversal\",\"occurredOn\":\"2026-01-15\",\"recordedAt\":\"2026-01-15T09:30:00.123Z\",\"note\":\"Typo\",\"tags\":[],"
            + "\"groupId\":null,\"source\":\"Manual\",\"externalId\":null,\"bucketId\":null,\"amount\":null,\"fromBucketId\":null,\"toBucketId\":null,\"fromAmount\":null,\"toAmount\":null,"
            + "\"fee\":null,\"rate\":null,\"reversedEntryId\":\"0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b\",\"reversedBy\":null},"
            + "{\"id\":\"0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b\",\"type\":\"Transfer\",\"occurredOn\":\"2026-01-14\",\"recordedAt\":\"2026-01-15T09:30:00.123Z\",\"note\":null,\"tags\":[\"move\"],"
            + "\"groupId\":null,\"source\":\"Manual\",\"externalId\":null,\"bucketId\":null,\"amount\":null,"
            + "\"fromBucketId\":\"0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2d\",\"toBucketId\":\"0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2e\","
            + "\"fromAmount\":{\"amount\":\"43.50\",\"currency\":\"PLN\"},\"toAmount\":{\"amount\":\"10.00\",\"currency\":\"EUR\"},\"fee\":{\"amount\":\"0.50\",\"currency\":\"PLN\"},"
            + "\"rate\":\"0.232558139535\",\"reversedEntryId\":null,\"reversedBy\":\"0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2c\"}],"
            + "\"nextCursor\":\"AQGQ\"}");
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
