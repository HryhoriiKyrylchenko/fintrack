using System.Net;
using System.Text;
using FinTrack.Shared.Infrastructure.Fx;
using FinTrack.Shared.Infrastructure.IntegrationTests.Outbox;
using FinTrack.Shared.Infrastructure.Mongo;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Shared.Kernel.Time;
using FinTrack.Tests.Common;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.IntegrationTests.Fx;

/// <summary>
/// The FX rate behaviour of plan 1a D8, without network: a rate fetched today is reused; otherwise
/// Frankfurter is asked (at most the configured time) and the answer stored per rate date; when that
/// fails the newest stored rates are used; with none stored the rate is unavailable. Cross rates go
/// through EUR. Logs never carry a rate or an amount.
/// </summary>
public sealed class FrankfurterFxRateProviderTests(MongoDatabaseFixture fixture) : IAsyncLifetime
{
    // FakeClock.DefaultStart is Thursday 2026-01-15 09:30 UTC; before 16:00 CET Frankfurter still
    // answers with Wednesday's rates.
    private const string Wednesday = """{"amount":1.0,"base":"EUR","date":"2026-01-14","rates":{"PLN":4.2135,"USD":1.0912}}""";
    private const string Thursday = """{"amount":1.0,"base":"EUR","date":"2026-01-15","rates":{"PLN":4.2301,"USD":1.0954}}""";

    private static readonly string[] RateTexts = ["4.2135", "1.0912", "4.2301", "1.0954", "4,2135", "4,2301"];

    private readonly FakeClock clock = new();
    private readonly FakeHandler handler = new();
    private readonly CapturingLoggerFactory logs = new();
    private readonly string collectionName = $"ref_it_{Guid.NewGuid():N}";
    private ReferenceCollection<FxRateSet> rates = null!;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SameCurrency_IsOne_WithoutAnyCall()
    {
        var rate = (await GetAsync(Currency.Pln, Currency.Pln)).Value;

        rate.Rate.ShouldBe(1m);
        rate.Date.ShouldBe(new DateOnly(2026, 1, 15));
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task NothingStored_AsksFrankfurter_ForEveryRegisteredCurrency_AndStoresTheAnswer()
    {
        handler.Answer(Wednesday);

        var rate = (await GetAsync(Currency.Eur, Currency.Pln)).Value;

        rate.ShouldBe(new FxRate(Currency.Eur, Currency.Pln, 4.2135m, new DateOnly(2026, 1, 14)));
        handler.Requests.ShouldBe(["https://frankfurter.test/v1/latest?base=EUR&symbols=PLN,USD"]);
        var stored = (await StoredAsync()).ShouldHaveSingleItem();
        stored["date"].AsString.ShouldBe("2026-01-14");
        stored["rates"]["PLN"].ShouldBe(new BsonDecimal128(4.2135m));
        stored["fetchedAt"].ToUniversalTime().ShouldBe(FakeClock.DefaultStart.UtcDateTime);
    }

    [Fact]
    public async Task CrossRates_GoThroughEur()
    {
        handler.Answer(Wednesday);

        (await GetAsync(Currency.Pln, Currency.Eur)).Value.Rate.ShouldBe(1m / 4.2135m);
        (await GetAsync(Currency.Pln, Currency.Usd)).Value.Rate.ShouldBe(1.0912m / 4.2135m);
        (await GetAsync(Currency.Usd, Currency.Pln)).Value.Rate.ShouldBe(4.2135m / 1.0912m);
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ACrossRate_ConvertsOnce_WithBankersRounding()
    {
        handler.Answer(Wednesday);
        var plnToUsd = (await GetAsync(Currency.Pln, Currency.Usd)).Value;

        var direct = plnToUsd.Convert(Money.From(1000m, Currency.Pln));

        // 1000 PLN = 237.33 EUR = 258.98 USD; the direct cross rate gives the exact quotient.
        direct.ShouldBe(Money.From(258.98m, Currency.Usd));
    }

    [Fact]
    public async Task FetchedToday_IsReused_WithoutASecondCall()
    {
        handler.Answer(Wednesday);
        await GetAsync(Currency.Eur, Currency.Pln);
        clock.Advance(TimeSpan.FromHours(14));

        (await GetAsync(Currency.Eur, Currency.Usd)).Value.Date.ShouldBe(new DateOnly(2026, 1, 14));

        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task FetchedYesterday_IsStale_AndTheNewAnswerIsStoredNextToIt()
    {
        handler.Answer(Wednesday);
        await GetAsync(Currency.Eur, Currency.Pln);
        clock.Advance(TimeSpan.FromDays(1));
        handler.Answer(Thursday);

        var rate = (await GetAsync(Currency.Eur, Currency.Pln)).Value;

        rate.ShouldBe(new FxRate(Currency.Eur, Currency.Pln, 4.2301m, new DateOnly(2026, 1, 15)));
        handler.Requests.Count.ShouldBe(2);
        (await StoredAsync()).Select(document => document["date"].AsString).ShouldBe(["2026-01-14", "2026-01-15"]);
    }

    [Fact]
    public async Task OnAWeekend_FridaysRatesCountAsFetchedToday()
    {
        clock.Set(new DateTimeOffset(2026, 1, 17, 8, 0, 0, TimeSpan.Zero));
        handler.Answer("""{"amount":1.0,"base":"EUR","date":"2026-01-16","rates":{"PLN":4.22,"USD":1.09}}""");

        (await GetAsync(Currency.Eur, Currency.Pln)).Value.Date.ShouldBe(new DateOnly(2026, 1, 16));
        clock.Advance(TimeSpan.FromHours(10));
        (await GetAsync(Currency.Eur, Currency.Pln)).Value.Date.ShouldBe(new DateOnly(2026, 1, 16));

        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task TheSameRateDateFetchedAgain_IsOneStoredSet_WithTheNewFetchTime()
    {
        handler.Answer(Wednesday);
        await GetAsync(Currency.Eur, Currency.Pln);
        var firstId = (await StoredAsync()).Single()["_id"];
        clock.Advance(TimeSpan.FromDays(1));
        handler.Answer(Wednesday);

        await GetAsync(Currency.Eur, Currency.Pln);

        var stored = (await StoredAsync()).ShouldHaveSingleItem();
        stored["_id"].ShouldBe(firstId);
        stored["fetchedAt"].ToUniversalTime().ShouldBe(clock.UtcNow.UtcDateTime);
    }

    public static TheoryData<string, string> Failures => new()
    {
        { "status 500", "status:500" },
        { "status 404", "status:404" },
        { "network", "network" },
        { "timeout", "timeout" },
        { "not JSON", "<html>Moved</html>" },
        { "empty", string.Empty },
        { "an array", "[]" },
        { "another base", """{"amount":1.0,"base":"USD","date":"2026-01-15","rates":{"PLN":3.86,"EUR":0.91}}""" },
        { "no base", """{"amount":1.0,"date":"2026-01-15","rates":{"PLN":4.23,"USD":1.09}}""" },
        { "no date", """{"amount":1.0,"base":"EUR","rates":{"PLN":4.23,"USD":1.09}}""" },
        { "a malformed date", """{"amount":1.0,"base":"EUR","date":"15.01.2026","rates":{"PLN":4.23,"USD":1.09}}""" },
        { "no rates", """{"amount":1.0,"base":"EUR","date":"2026-01-15"}""" },
        { "a missing currency", """{"amount":1.0,"base":"EUR","date":"2026-01-15","rates":{"PLN":4.23}}""" },
        { "a zero rate", """{"amount":1.0,"base":"EUR","date":"2026-01-15","rates":{"PLN":0,"USD":1.09}}""" },
        { "a negative rate", """{"amount":1.0,"base":"EUR","date":"2026-01-15","rates":{"PLN":-4.23,"USD":1.09}}""" },
        { "a rate as text", """{"amount":1.0,"base":"EUR","date":"2026-01-15","rates":{"PLN":"4.23","USD":1.09}}""" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task WhenFrankfurterFails_TheNewestStoredRatesAreUsed(string failure, string answer)
    {
        _ = failure;
        handler.Answer("""{"amount":1.0,"base":"EUR","date":"2026-01-13","rates":{"PLN":4.1999,"USD":1.0888}}""");
        handler.Answer(Wednesday);
        await GetAsync(Currency.Eur, Currency.Pln);
        clock.Advance(TimeSpan.FromDays(1));
        await GetAsync(Currency.Eur, Currency.Pln);
        clock.Advance(TimeSpan.FromDays(1));
        handler.Answer(answer);

        var rate = (await GetAsync(Currency.Eur, Currency.Pln)).Value;

        rate.ShouldBe(new FxRate(Currency.Eur, Currency.Pln, 4.2135m, new DateOnly(2026, 1, 14)));
        (await StoredAsync()).Count.ShouldBe(2);
        logs.Lines.ShouldContain(line => line.StartsWith("Warning: FX rate request failed", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task WhenFrankfurterFails_AndNothingIsStored_TheRateIsUnavailable(string failure, string answer)
    {
        _ = failure;
        handler.Answer(answer);

        (await GetAsync(Currency.Eur, Currency.Pln)).ShouldFailWith(ErrorCodes.FxRateUnavailable, ErrorKind.Unavailable);

        (await StoredAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task TheCallersCancellation_IsNotAFailureOfFrankfurter()
    {
        // The caller gives up while the request is open: that is no reason to fall back to stored rates.
        using var caller = new CancellationTokenSource();
        handler.OnSend = caller.Cancel;
        handler.Answer("timeout");

        await Should.ThrowAsync<OperationCanceledException>(() => Provider().GetRateAsync(Currency.Eur, Currency.Pln, caller.Token));
        logs.Lines.ShouldNotContain(line => line.Contains("FX rate request failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Logs_NeverCarryARate()
    {
        handler.Answer(Wednesday);
        await GetAsync(Currency.Eur, Currency.Pln);
        clock.Advance(TimeSpan.FromDays(1));
        handler.Answer("status:503");
        await GetAsync(Currency.Pln, Currency.Usd);

        logs.Lines.ShouldNotBeEmpty();
        logs.Lines.ShouldAllBe(line => !RateTexts.Any(text => line.Contains(text, StringComparison.Ordinal)));
    }

    /// <summary>
    /// The one call to the real service, run by hand (<c>--filter-method "*AgainstTheRealService*"</c>)
    /// and recorded in the plan's Result note; it is never part of a normal run.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task AgainstTheRealService()
    {
        using var http = new HttpClient { BaseAddress = new Uri("https://api.frankfurter.dev/v1/"), Timeout = TimeSpan.FromSeconds(5) };
        var provider = new FrankfurterFxRateProvider(http, rates, new SystemClock(), logs.CreateLogger<FrankfurterFxRateProvider>());

        var rate = (await provider.GetRateAsync(Currency.Pln, Currency.Usd, Cancellation)).Value;

        rate.Rate.ShouldBeGreaterThan(0m);
        rate.Date.ShouldBeGreaterThan(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7)));
        TestContext.Current.SendDiagnosticMessage($"Frankfurter answered with rate date {rate.Date:yyyy-MM-dd}.");
    }

    public async ValueTask InitializeAsync() => rates = await NewCollectionAsync();

    public ValueTask DisposeAsync()
    {
        handler.Dispose();
        logs.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<FluentResults.Result<FxRate>> GetAsync(Currency from, Currency to) =>
        await Provider().GetRateAsync(from, to, Cancellation);

    // A new provider per call, as the Api creates one per request; the stored rates connect them.
    private FrankfurterFxRateProvider Provider()
    {
        var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://frankfurter.test/v1/"), Timeout = TimeSpan.FromMilliseconds(500) };
        return new FrankfurterFxRateProvider(http, rates, clock, logs.CreateLogger<FrankfurterFxRateProvider>());
    }

    private async Task<ReferenceCollection<FxRateSet>> NewCollectionAsync()
    {
        await FxRateIndexInitializer.CreateIndexAsync(fixture.Database, collectionName, Cancellation);
        return new ReferenceCollection<FxRateSet>(fixture.Database, collectionName);
    }

    private Task<List<BsonDocument>> StoredAsync() =>
        fixture.Database.GetCollection<BsonDocument>(collectionName).Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("date", 1)).ToListAsync(Cancellation);

    /// <summary>
    /// Answers each request with the next queued answer: a JSON body, <c>status:NNN</c>,
    /// <c>network</c> (the connection fails) or <c>timeout</c> (no answer within the client's time).
    /// </summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<string> answers = new();

        public List<string> Requests { get; } = [];

        /// <summary>Runs when a request arrives, before it is answered.</summary>
        public Action? OnSend { get; set; }

        public void Answer(string answer) => answers.Enqueue(answer);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            OnSend?.Invoke();
            var answer = answers.Dequeue();
            switch (answer)
            {
                case "network":
                    throw new HttpRequestException("No connection.");
                case "timeout":
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                    throw new InvalidOperationException("The client should have given up.");
                case var status when status.StartsWith("status:", StringComparison.Ordinal):
                    return new HttpResponseMessage((HttpStatusCode)int.Parse(status["status:".Length..], System.Globalization.CultureInfo.InvariantCulture));
                default:
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            }
        }
    }
}
