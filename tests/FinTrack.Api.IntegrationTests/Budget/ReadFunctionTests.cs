using System.Text;
using FinTrack.Api.Budget;
using FinTrack.Api.Http;
using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Contracts.V1.Journal;
using FinTrack.Budget.Contracts.V1.Overview;
using FinTrack.Shared.Infrastructure.Fx;
using FinTrack.Shared.Infrastructure.Mongo;
using FinTrack.Shared.Infrastructure.Owners;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using BucketKind = FinTrack.Budget.Contracts.V1.Buckets.BucketKind;
using CreateBucket = FinTrack.Budget.Application.Buckets.CreateBucket;

namespace FinTrack.Api.IntegrationTests.Budget;

/// <summary>
/// <c>GET /api/v1/overview</c> and <c>GET /api/v1/journal</c> through the Api's own container: query reading,
/// the pipeline (the journal's validator), the handlers and MongoDB. Conversions use the real rate provider
/// with its source made unreachable, so it falls back to rates stored in the test database (offline), or has
/// none at all; the network is never used.
/// </summary>
public sealed class ReadFunctionTests(ApiFixture fixture)
{
    private static readonly DateOnly StoredRateDate = new(2026, 1, 14);

    private readonly OwnerId owner = OwnerId.From(Guid.NewGuid());

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static DateOnly Yesterday => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));

    [Fact]
    public async Task Overview_BeforeTheFirstRun_Is404SettingsNotFound()
    {
        await using var api = Offline();
        var services = api.Services;

        Code(await OverviewAsync(services), 404).ShouldBe("settings.not_found");
    }

    [Fact]
    public async Task Overview_Offline_ConvertsWithTheNewestStoredRate()
    {
        await using var api = Offline();
        var services = api.Services;
        await StoreRatesAsync(services);
        await SaveSettingsAsync(services, "PLN");
        await DepositAsync(services, Money.From(10.00m, Currency.Pln));
        await DepositAsync(services, Money.From(2.00m, Currency.Eur));
        await DepositAsync(services, Money.From(4.00m, Currency.Usd));

        var overview = Ok<BudgetOverviewDto>(await OverviewAsync(services));

        // EUR 2.00 × 4.2735 = 8.547 → 8.55; USD through EUR: 4.2735 ÷ 1.1 = 3.885, 4.00 × 3.885 = 15.54.
        overview.Totals.Select(total => (total.Currency, total.Available, total.Conversion?.Amount, total.Conversion?.RateDate)).ShouldBe(
        [
            (Currency.Pln, Money.From(10.00m, Currency.Pln), null, null),
            (Currency.Eur, Money.From(2.00m, Currency.Eur), Money.From(8.55m, Currency.Pln), StoredRateDate),
            (Currency.Usd, Money.From(4.00m, Currency.Usd), Money.From(15.54m, Currency.Pln), StoredRateDate),
        ]);
        overview.ReportingTotal.ShouldBe(new ReportingTotalDto(Money.From(34.09m, Currency.Pln), StoredRateDate, null));
        overview.Buckets.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Overview_WithoutAnyRate_Is200_WithTheReasonInsteadOfTheTotal()
    {
        await using var api = Offline();
        var services = api.Services;
        await SaveSettingsAsync(services, "PLN");
        await DepositAsync(services, Money.From(10.00m, Currency.Pln));
        await DepositAsync(services, Money.From(2.00m, Currency.Eur));

        var overview = Ok<BudgetOverviewDto>(await OverviewAsync(services));

        overview.ReportingTotal.ShouldBe(new ReportingTotalDto(null, null, "fx.rate_unavailable"));
        overview.Totals.Select(total => total.Available).ShouldBe([Money.From(10.00m, Currency.Pln), Money.From(2.00m, Currency.Eur)]);
    }

    [Fact]
    public async Task Journal_IsReadPageByPage_ThroughTheCursor()
    {
        await SaveSettingsAsync(fixture.Services, "PLN");
        for (var count = 1; count <= 5; count++)
        {
            await DepositAsync(fixture.Services, Money.From(count, Currency.Pln));
        }

        var first = Ok<JournalPageDto>(await JournalAsync("?limit=2"));
        var second = Ok<JournalPageDto>(await JournalAsync($"?limit=2&cursor={first.NextCursor}"));
        var third = Ok<JournalPageDto>(await JournalAsync($"?limit=2&cursor={second.NextCursor}"));

        first.Entries.Concat(second.Entries).Concat(third.Entries).Select(entry => entry.Amount!.Amount).ShouldBe([5m, 4m, 3m, 2m, 1m]);
        third.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task Journal_WithoutParameters_IsTheFirstPage()
    {
        var page = Ok<JournalPageDto>(await JournalAsync(string.Empty));

        page.Entries.ShouldBeEmpty();
        page.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task Journal_ForAnUnknownBucket_Is404() =>
        Code(await JournalAsync($"?bucketId={Guid.CreateVersion7()}"), 404).ShouldBe("bucket.not_found");

    [Theory]
    [InlineData("?limit=ten", "limit", RequestValues.QueryFormatRule)]
    [InlineData("?limit=05", "limit", RequestValues.QueryFormatRule)]
    [InlineData("?from=15.01.2026", "from", RequestValues.QueryFormatRule)]
    [InlineData("?to=2026-01-15&to=2026-01-16", "to", RequestValues.QueryFormatRule)]
    [InlineData("?bucketId=nope", "bucketId", RequestValues.QueryFormatRule)]
    [InlineData("?limit=0", "limit", "InclusiveBetweenValidator")]
    [InlineData("?limit=201", "limit", "InclusiveBetweenValidator")]
    [InlineData("?from=2026-01-16&to=2026-01-15", "to", GetJournalValidator.FromNotAfterTo)]
    [InlineData("?cursor=not-a-cursor", "cursor", JournalCursor.FormatRule)]
    [InlineData("?cursor=", "cursor", JournalCursor.FormatRule)]
    public async Task Journal_WithAParameterThatDoesNotFit_Is400ValidationFailed_AtTheParameter(string query, string field, string rule)
    {
        var problem = Problem(await JournalAsync(query), 400);
        var parameters = problem.Extensions["params"].ShouldBeAssignableTo<IReadOnlyDictionary<string, object>>()!;

        problem.Extensions["code"].ShouldBe("validation.failed");
        parameters["field"].ShouldBe(field);
        parameters["rule"].ShouldBe(rule);
    }

    // The real provider, with a source that refuses connections at once: every fetch fails, as offline.
    private OfflineApi Offline() => new(ApiFixture.BuildServices(fixture.ConnectionString, $"fintrack_api_fx_{Guid.NewGuid():N}"[..36], more: new Dictionary<string, string?>
    {
        ["FxRates:BaseAddress"] = "http://127.0.0.1:9/",
        ["FxRates:Timeout"] = "00:00:02",
    }));

    // An invented EUR-based rate set from an earlier day, as an earlier fetch would have stored it.
    private static async Task StoreRatesAsync(ServiceProvider services)
    {
        await services.GetRequiredService<MongoIndexRunner>().EnsureCreatedAsync(Cancellation);
        var rates = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["PLN"] = 4.2735m, ["USD"] = 1.1m };
        var set = new FxRateSet(Guid.CreateVersion7(), FxRateSet.CurrentSchemaVersion, Currency.Eur, StoredRateDate, rates, new DateTimeOffset(2026, 1, 14, 16, 0, 0, TimeSpan.Zero));
        await services.GetRequiredService<IMongoDatabase>().GetCollection<FxRateSet>(FxRateSet.CollectionName).InsertOneAsync(set, cancellationToken: Cancellation);
    }

    private async Task SaveSettingsAsync(IServiceProvider services, string currency)
    {
        await using var scope = await ScopeAsync(services);
        var request = JsonRequest($$"""{"reportingCurrency":"{{currency}}","timeZone":"Etc/UTC","locale":"pl-PL"}""");
        (await ActivatorUtilities.CreateInstance<SaveOwnerSettingsFunction>(scope.ServiceProvider).Run(request, Cancellation)).ShouldBeOfType<NoContentResult>();
    }

    // A deposit into a new bucket of the amount's currency.
    private async Task DepositAsync(IServiceProvider services, Money amount)
    {
        await using var scope = await ScopeAsync(services);
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var bucket = Guid.CreateVersion7();
        (await sender.Send(new CreateBucket(bucket, $"Bucket {Guid.NewGuid():N}"[..20], amount.Currency, BucketKind.Savings, null), Cancellation)).IsSuccess.ShouldBeTrue();
        (await sender.Send(new AddDeposit(Guid.CreateVersion7(), amount, Yesterday, bucket, null, null, []), Cancellation)).IsSuccess.ShouldBeTrue();
    }

    private async Task<IActionResult> OverviewAsync(IServiceProvider services)
    {
        await using var scope = await ScopeAsync(services);
        return await ActivatorUtilities.CreateInstance<GetBudgetOverviewFunction>(scope.ServiceProvider).Run(new DefaultHttpContext().Request, Cancellation);
    }

    private async Task<IActionResult> JournalAsync(string query)
    {
        await using var scope = await ScopeAsync(fixture.Services);
        var request = new DefaultHttpContext().Request;
        request.QueryString = new QueryString(query.Length == 0 ? null : query);
        return await ActivatorUtilities.CreateInstance<GetJournalFunction>(scope.ServiceProvider).Run(request, Cancellation);
    }

    private async Task<AsyncServiceScope> ScopeAsync(IServiceProvider services)
    {
        await services.GetRequiredService<MongoIndexRunner>().EnsureCreatedAsync(Cancellation);
        var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedOwnerContext>().Set(owner);
        return scope;
    }

    private static HttpRequest JsonRequest(string json)
    {
        var request = new DefaultHttpContext().Request;
        request.ContentType = "application/json";
        request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return request;
    }

    private static T Ok<T>(IActionResult result) => result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<T>();

    private static string Code(IActionResult result, int status) => (string)Problem(result, status).Extensions["code"]!;

    private static ProblemDetails Problem(IActionResult result, int status)
    {
        var objectResult = result.ShouldBeOfType<ObjectResult>();
        objectResult.StatusCode.ShouldBe(status);
        return objectResult.Value.ShouldBeOfType<ProblemDetails>();
    }

    /// <summary>An Api container with its own database, which is dropped when the test ends, whatever its outcome.</summary>
    private sealed class OfflineApi(ServiceProvider services) : IAsyncDisposable
    {
        public ServiceProvider Services => services;

        public async ValueTask DisposeAsync()
        {
            var database = services.GetRequiredService<IMongoDatabase>().DatabaseNamespace.DatabaseName;
            await services.GetRequiredService<IMongoClient>().DropDatabaseAsync(database);
            await services.DisposeAsync();
        }
    }
}
