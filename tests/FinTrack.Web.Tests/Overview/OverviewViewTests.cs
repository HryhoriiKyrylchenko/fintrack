using System.Net;
using System.Text;
using FinTrack.Budget.Contracts.V1.Buckets;
using FinTrack.Budget.Contracts.V1.Overview;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Web.Api;
using FinTrack.Web.Overview;

namespace FinTrack.Web.Tests.Overview;

/// <summary>
/// What the Overview shows from the Api's answer (plan 1a step 15, DESIGN 8): the total in the reporting
/// currency with its rate date or the reason it is missing, the other currencies only when there are any,
/// a card per bucket ("Invested" for investment buckets), and <c>Unallocated</c> only when it holds money.
/// </summary>
public class OverviewViewTests
{
    private static readonly DateOnly Today = new(2026, 1, 15);
    private static readonly DateOnly RateDate = new(2026, 1, 14);

    [Fact]
    public void ConvertedTotal_IsTheKeyFigure_WithItsRateDate()
    {
        var view = OverviewView.From(Overview(
            [Total(Pln(10m)), Total(Eur(2m), new ConversionDto(Pln(8.55m), 4.2735m, RateDate))],
            new ReportingTotalDto(Pln(18.55m), RateDate, null),
            [Savings("Cushion", Pln(10m)), Savings("Euro", Eur(2m))]));

        view.Total.ShouldBe(Pln(18.55m));
        view.RateDate.ShouldBe(RateDate);
        view.TotalUnavailableReason.ShouldBeNull();
        view.ShowCurrencyTotals.ShouldBeTrue();
        view.CurrencyTotals.Select(total => total.Currency).ShouldBe([Currency.Pln, Currency.Eur]);
    }

    [Fact]
    public void OneCurrency_ShowsNoCurrencyList_AndNoRateDate()
    {
        var view = OverviewView.From(Overview([Total(Pln(10m))], new ReportingTotalDto(Pln(10m), null, null), [Savings("Cushion", Pln(10m))]));

        view.ShowCurrencyTotals.ShouldBeFalse();
        view.RateDate.ShouldBeNull();
        view.Total.ShouldBe(Pln(10m));
    }

    [Fact]
    public void MissingRate_GivesTheReason_AndKeepsTheCurrencyTotals()
    {
        var view = OverviewView.From(Overview(
            [Total(Pln(10m)), Total(Eur(2m))],
            new ReportingTotalDto(null, null, "fx.rate_unavailable"),
            [Savings("Cushion", Pln(10m)), Savings("Euro", Eur(2m))]));

        view.Total.ShouldBeNull();
        view.TotalUnavailableReason.ShouldBe("fx.rate_unavailable");
        view.ShowCurrencyTotals.ShouldBeTrue();
        view.CurrencyTotals.Count.ShouldBe(2);
    }

    [Fact]
    public void ReportingCurrencyWithoutABucket_StillListsTheOthers()
    {
        var view = OverviewView.From(Overview(
            [Total(Eur(2m), new ConversionDto(Pln(8.55m), 4.2735m, RateDate))],
            new ReportingTotalDto(Pln(8.55m), RateDate, null),
            [Savings("Euro", Eur(2m))]));

        view.ShowCurrencyTotals.ShouldBeTrue();
    }

    [Fact]
    public void Cards_FollowTheApiOrder_SavingsAreAvailable_InvestmentsAreInvested()
    {
        var cushion = Savings("Cushion", Pln(10m));
        var crypto = Savings("Crypto", Usd(-3m)) with { Kind = BucketKind.Investment, Profile = InvestmentProfile.Crypto };

        var view = OverviewView.From(Overview([Total(Pln(10m)), Total(Usd(-3m))], new ReportingTotalDto(null, null, "fx.rate_unavailable"), [cushion, crypto]));

        view.Cards.Select(card => card.Bucket.Id).ShouldBe([cushion.Id, crypto.Id]);
        view.Cards[0].AmountLabelKey.ShouldBe(OverviewCard.AvailableKey);
        view.Cards[1].AmountLabelKey.ShouldBe(OverviewCard.InvestedKey);
        view.Cards[1].Amount.ShouldBe(Usd(-3m));
    }

    [Fact]
    public void Unallocated_IsShownOnlyWhenItHoldsMoney()
    {
        var empty = Unallocated(Pln(0m));
        var holding = Unallocated(Eur(0.01m));

        var view = OverviewView.From(Overview([Total(Pln(0m)), Total(Eur(0.01m))], new ReportingTotalDto(null, null, "fx.rate_unavailable"),
            [Savings("Cushion", Pln(0m)), empty, holding]));

        view.Cards.Select(card => card.Bucket.Id).ShouldNotContain(empty.Id);
        view.Cards.Select(card => card.Bucket.Id).ShouldContain(holding.Id);
        view.Cards.Count.ShouldBe(2);
    }

    [Fact]
    public void NoBuckets_IsEmpty()
    {
        var view = OverviewView.From(Overview([], new ReportingTotalDto(Pln(0m), null, null), []));

        view.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void OnlyAnEmptyUnallocated_IsEmptyToo()
    {
        var view = OverviewView.From(Overview([Total(Pln(0m))], new ReportingTotalDto(Pln(0m), null, null), [Unallocated(Pln(0m))]));

        view.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void AnEmptyBucketOfTheOwner_IsNotEmpty()
    {
        var view = OverviewView.From(Overview([Total(Pln(0m))], new ReportingTotalDto(Pln(0m), null, null), [Savings("Cushion", Pln(0m))]));

        view.IsEmpty.ShouldBeFalse();
        view.Cards.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Client_GetsTheOverview()
    {
        Uri? asked = null;
        var json = "{\"reportingCurrency\":\"PLN\",\"asOf\":\"2026-01-15\",\"totals\":[],"
            + "\"reportingTotal\":{\"amount\":{\"amount\":\"0.00\",\"currency\":\"PLN\"},\"rateDate\":null,\"unavailableReason\":null},\"buckets\":[]}";
        var client = new OverviewClient(new ApiClient(new HttpClient(new Handler(request =>
        {
            asked = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }))
        { BaseAddress = new Uri("http://localhost:7071/api/") }));

        var result = await client.GetAsync(TestContext.Current.CancellationToken);

        asked.ShouldBe(new Uri("http://localhost:7071/api/v1/overview"));
        result.Value!.AsOf.ShouldBe(Today);
    }

    private static BudgetOverviewDto Overview(IReadOnlyList<CurrencyTotalDto> totals, ReportingTotalDto total, IReadOnlyList<BucketSummaryDto> buckets) =>
        new(Currency.Pln, Today, totals, total, buckets);

    private static CurrencyTotalDto Total(Money available, ConversionDto? conversion = null) => new(available.Currency, available, conversion);

    private static BucketSummaryDto Savings(string name, Money available) =>
        new(Guid.CreateVersion7(), name, available.Currency, BucketKind.Savings, null, BucketSystemRole.None, available, available, Money.Zero(available.Currency));

    private static BucketSummaryDto Unallocated(Money available) =>
        Savings("unused", available) with { Name = null, SystemRole = BucketSystemRole.Unallocated };

    private static Money Pln(decimal amount) => Money.From(amount, Currency.Pln);

    private static Money Eur(decimal amount) => Money.From(amount, Currency.Eur);

    private static Money Usd(decimal amount) => Money.From(amount, Currency.Usd);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
