using System.Globalization;
using System.Net;
using System.Text;
using FinTrack.Budget.Contracts.V1.Buckets;
using FinTrack.Budget.Contracts.V1.Loans;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Web.Api;
using FinTrack.Web.Loans;
using FinTrack.Web.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinTrack.Web.Tests.Loans;

/// <summary>
/// The Loans screen's plain parts (plan 1b step 7): a loan's buckets by name ("Outside the app" for none), its line for a
/// choice list, the order (open loans first), and the Api calls (the list, and a loan's history read page by page).
/// </summary>
public sealed class LoanRowTests : IDisposable
{
    private const string Base = "http://localhost:7071/api/";
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly DateOnly Today = new(2026, 1, 15);

    private static readonly BucketDto Cushion = Bucket("Cushion", BucketStatus.Active);
    private static readonly BucketDto Old = Bucket("Old", BucketStatus.Archived);

    private readonly ServiceProvider services = new ServiceCollection()
        .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
        .AddLocalization()
        .BuildServiceProvider();

    private readonly List<string> asked = [];

    private IStringLocalizer<SharedResource> L => services.GetRequiredService<IStringLocalizer<SharedResource>>();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void ALoan_NamesItsBuckets_ArchivedOnesToo_AndOutsideTheApp()
    {
        var intoOld = Row(Loan(Old.Id, LoanStatus.Open));
        var toOutside = Row(Loan(null, LoanStatus.Open));

        intoOld.LenderName(L).ShouldBe("Cushion");
        intoOld.DestinationName(L).ShouldBe("Old");
        toOutside.DestinationName(L).ShouldBe("Outside the app");
        Row(Loan(Guid.CreateVersion7(), LoanStatus.Open)).DestinationName(L).ShouldBe("Unknown bucket");
    }

    [Fact]
    public void ALoansLine_NamesBothSides_ThePrincipal_AndTheDate() =>
        Row(Loan(null, LoanStatus.Open)).Describe(L, Polish)
            .ShouldBe("Cushion \u2192 Outside the app: 1\u00A0200,00\u00A0PLN, " + Today.AddDays(-10).ToString("d", Polish));

    [Fact]
    public void OpenLoans_ComeFirst_EachGroupInTheApisOrder()
    {
        var closedFirst = Loan(null, LoanStatus.Closed);
        var openSecond = Loan(null, LoanStatus.Open);
        var closedThird = Loan(null, LoanStatus.Closed);
        var openFourth = Loan(null, LoanStatus.Open);

        var rows = LoanRow.Order([closedFirst, openSecond, closedThird, openFourth], Buckets());

        rows.Select(row => row.Loan.Id).ShouldBe([openSecond.Id, openFourth.Id, closedFirst.Id, closedThird.Id]);
        rows.Select(row => row.IsOpen).ShouldBe([true, true, false, false]);
    }

    [Theory]
    [InlineData(false, Base + "v1/loans")]
    [InlineData(true, Base + "v1/loans?includeClosed=true")]
    public async Task List_AsksForClosedLoansOnlyWhenWanted(bool includeClosed, string expected)
    {
        var answer = "[{\"id\":\"0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b\",\"lenderBucketId\":\"0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2c\",\"toBucketId\":null,"
            + "\"principal\":{\"amount\":\"10.00\",\"currency\":\"PLN\"},\"borrowedOn\":\"2026-01-05\",\"repaid\":{\"amount\":\"0.00\",\"currency\":\"PLN\"},"
            + "\"outstanding\":{\"amount\":\"10.00\",\"currency\":\"PLN\"},\"status\":\"Open\",\"closedOn\":null,\"note\":null}]";

        var result = await Client(_ => answer).ListAsync(includeClosed, Cancellation);

        result.Value!.ShouldHaveSingleItem().Outstanding.ShouldBe(Money.From(10m, Currency.Pln));
        asked.ShouldBe([expected]);
    }

    [Fact]
    public async Task History_ReadsEveryPageOfTheLoansJournal()
    {
        var loan = Guid.Parse("0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b");
        var client = Client(uri => uri.Contains("cursor=", StringComparison.Ordinal)
            ? "{\"entries\":[" + Entry("0190a1b2-c3d4-7e5f-8a6b-000000000002", "Borrow", loan) + "],\"nextCursor\":null}"
            : "{\"entries\":[" + Entry("0190a1b2-c3d4-7e5f-8a6b-000000000001", "Repayment", loan) + "],\"nextCursor\":\"AQ\"}");

        var history = await client.HistoryAsync(loan, Cancellation);

        history.Value!.Select(entry => entry.Type.ToString()).ShouldBe(["Repayment", "Borrow"]);
        asked.ShouldBe(
        [
            Base + "v1/journal?loanId=0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b&limit=25",
            Base + "v1/journal?loanId=0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b&limit=25&cursor=AQ",
        ]);
    }

    [Fact]
    public async Task History_StopsAtTheFirstFailure_WithItsCode()
    {
        var history = await new LoansClient(new ApiClient(new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"type\":\"urn:fintrack:error:loan.not_found\",\"title\":\"loan.not_found\",\"status\":404,\"code\":\"loan.not_found\",\"params\":{},\"errors\":[]}",
                Encoding.UTF8, "application/problem+json"),
        })) { BaseAddress = new Uri(Base) })).HistoryAsync(Guid.CreateVersion7(), Cancellation);

        history.IsSuccess.ShouldBeFalse();
        history.ErrorCode.ShouldBe("loan.not_found");
    }

    public void Dispose() => services.Dispose();

    private static LoanRow Row(LoanDto loan) => new(loan, Buckets());

    private static Dictionary<Guid, BucketDto> Buckets() => new() { [Cushion.Id] = Cushion, [Old.Id] = Old };

    private static LoanDto Loan(Guid? to, LoanStatus status) =>
        new(Guid.CreateVersion7(), Cushion.Id, to, Money.From(1200m, Currency.Pln), Today.AddDays(-10), Money.Zero(Currency.Pln), Money.From(1200m, Currency.Pln), status, null, null);

    private static BucketDto Bucket(string name, BucketStatus status) =>
        new(Guid.CreateVersion7(), name, Currency.Pln, BucketKind.Savings, null, status, BucketSystemRole.None);

    private static string Entry(string id, string type, Guid loan) =>
        "{\"id\":\"" + id + "\",\"type\":\"" + type + "\",\"occurredOn\":\"2026-01-10\",\"recordedAt\":\"2026-01-10T09:30:00Z\",\"note\":null,\"tags\":[],"
        + "\"groupId\":null,\"source\":\"Manual\",\"externalId\":null,\"bucketId\":null,\"amount\":{\"amount\":\"10.00\",\"currency\":\"PLN\"},"
        + "\"fromBucketId\":null,\"toBucketId\":null,\"fromAmount\":null,\"toAmount\":null,\"fee\":null,\"rate\":null,\"reversedEntryId\":null,"
        + "\"reversedBy\":null,\"direction\":null,\"loanId\":\"" + loan + "\"}";

    private LoansClient Client(Func<string, string> answer) =>
        new(new ApiClient(new HttpClient(new Handler(request =>
        {
            var uri = request.RequestUri!.ToString();
            asked.Add(uri);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer(uri), Encoding.UTF8, "application/json") };
        })) { BaseAddress = new Uri(Base) }));

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
