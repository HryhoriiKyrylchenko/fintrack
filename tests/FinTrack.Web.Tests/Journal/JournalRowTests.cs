using System.Globalization;
using System.Net;
using System.Text;
using FinTrack.Budget.Contracts.V1.Buckets;
using FinTrack.Budget.Contracts.V1.Journal;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Web.Api;
using FinTrack.Web.Formatting;
using FinTrack.Web.Journal;
using FinTrack.Web.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinTrack.Web.Tests.Journal;

/// <summary>
/// The Journal screen's plain parts (plan 1a step 17): how an entry reads (type, buckets, signed amount, the
/// link between a reversed entry and its reversal), when it can be reversed, the filter, the reversal form and
/// the Api calls.
/// </summary>
public sealed class JournalRowTests : IDisposable
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly DateOnly Today = new(2026, 1, 15);
    private static readonly DateTimeOffset Recorded = new(2026, 1, 15, 9, 30, 0, TimeSpan.Zero);

    private static readonly BucketDto Cushion = Bucket("Cushion", Currency.Pln);
    private static readonly BucketDto Holiday = Bucket("Holiday", Currency.Eur);
    private static readonly BucketDto Old = Bucket("Old", Currency.Pln) with { Status = BucketStatus.Archived };
    private static readonly BucketDto UnallocatedPln = Bucket(null, Currency.Pln) with { SystemRole = BucketSystemRole.Unallocated };

    private readonly ServiceProvider services = new ServiceCollection()
        .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
        .AddLocalization()
        .BuildServiceProvider();

    private readonly List<string> asked = [];

    private IStringLocalizer<SharedResource> L => services.GetRequiredService<IStringLocalizer<SharedResource>>();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public static TheoryData<JournalEntryType> EntryTypes => [.. Enum.GetValues<JournalEntryType>()];

    // Every type the Api can send has a name and a name in a sentence, so the Journal never shows a resource key
    // (Adjustment from plan 1b step 4; its row, with the signed amount, comes with the forms in step 7).
    [Theory]
    [MemberData(nameof(EntryTypes))]
    public void EveryEntryType_HasItsTexts(JournalEntryType type)
    {
        L[JournalRow.TypeKeyOf(type)].ResourceNotFound.ShouldBeFalse();
        L["Journal.TheType." + type].ResourceNotFound.ShouldBeFalse();
    }

    [Theory]
    [InlineData(JournalEntryType.OpeningBalance, "Opening balance", "+5,00")]
    [InlineData(JournalEntryType.Deposit, "Deposit", "+5,00")]
    [InlineData(JournalEntryType.Spend, "Spend", "\u22125,00")]
    public void SingleBucketEntry_ReadsAsItsTypeBucketAndSignedAmount(JournalEntryType type, string label, string signedStart)
    {
        var row = Row(Single(type, Cushion, 5m));

        L[row.TypeKey].Value.ShouldBe(label);
        row.Describe(L, Polish).ShouldBe("Cushion");
        row.SignedAmount.ShouldNotBeNull();
        MoneyFormatter.Format(row.SignedAmount, Polish, MoneySign.Always).ShouldStartWith(signedStart);
    }

    // Until the Journal shows plan details (plan 1b step 16), a plan entry reads as its type and its plan's bucket, and moves no money.
    [Theory]
    [InlineData(JournalEntryType.PlanCreated, "Plan created")]
    [InlineData(JournalEntryType.PlanAmountChanged, "Plan amount changed")]
    [InlineData(JournalEntryType.PlanEndChanged, "Plan end changed")]
    [InlineData(JournalEntryType.PlanPaused, "Plan paused")]
    [InlineData(JournalEntryType.PlanPauseChanged, "Plan pause changed")]
    [InlineData(JournalEntryType.ShortfallWaived, "Shortfall written off")]
    public void PlanEntry_ReadsAsItsTypeAndBucket_WithoutAnAmount(JournalEntryType type, string label)
    {
        var row = Row(Single(type, Cushion, 500m) with { PlanId = Guid.CreateVersion7() });

        L[row.TypeKey].Value.ShouldBe(label);
        row.Describe(L, Polish).ShouldBe("Cushion");
        row.SignedAmount.ShouldBeNull();
        row.MovedAmount.ShouldBeNull();
    }

    [Theory]
    [InlineData(AdjustmentDirection.Increase, "+12,50")]
    [InlineData(AdjustmentDirection.Decrease, "\u221212,50")]
    public void Adjustment_ReadsAsItsBucket_SignedByItsDirection(AdjustmentDirection direction, string signedStart)
    {
        var row = Row(Single(JournalEntryType.Adjustment, Cushion, 12.50m) with { Direction = direction });

        L[row.TypeKey].Value.ShouldBe("Adjustment");
        row.Describe(L, Polish).ShouldBe("Cushion");
        MoneyFormatter.Format(row.SignedAmount!, Polish, MoneySign.Always).ShouldStartWith(signedStart);
        row.MovedAmount.ShouldBeNull();
    }

    [Fact]
    public void BorrowAndRepayment_ReadFromAndTo_WithOutsideTheAppForAMissingSide_AndTheirAmount()
    {
        var loan = Guid.CreateVersion7();
        var intoBucket = Row(Loan(JournalEntryType.Borrow, loan, Cushion.Id, UnallocatedPln.Id, 300m));
        var toOutside = Row(Loan(JournalEntryType.Borrow, loan, Cushion.Id, null, 50m));
        var fromOutside = Row(Loan(JournalEntryType.Repayment, loan, null, Cushion.Id, 20m));

        L[intoBucket.TypeKey].Value.ShouldBe("Borrow");
        L[fromOutside.TypeKey].Value.ShouldBe("Repayment");
        intoBucket.Describe(L, Polish).ShouldBe("Cushion \u2192 Unallocated (PLN)");
        toOutside.Describe(L, Polish).ShouldBe("Cushion \u2192 Outside the app");
        fromOutside.Describe(L, Polish).ShouldBe("Outside the app \u2192 Cushion");
        intoBucket.SignedAmount.ShouldBeNull();
        intoBucket.MovedAmount.ShouldBe(Money.From(300m, Currency.Pln));
        fromOutside.MovedAmount.ShouldBe(Money.From(20m, Currency.Pln));
    }

    [Fact]
    public void BorrowAndRepayment_AreReversibleUnlessABucketIsArchived_AndTheirReversalTitleNamesBothSides()
    {
        var loan = Guid.CreateVersion7();
        var repayment = Loan(JournalEntryType.Repayment, loan, null, Cushion.Id, 20m) with { OccurredOn = new DateOnly(2026, 1, 14) };

        Row(repayment).CanReverse.ShouldBeTrue();
        Row(Loan(JournalEntryType.Borrow, loan, Cushion.Id, Old.Id, 1m)).CanReverse.ShouldBeFalse();
        Row(repayment).ReversalTitle(L, Polish).ShouldBe("Reverse the repayment of 20,00\u00A0PLN from Outside the app to Cushion on 14.01.2026?");
    }

    [Fact]
    public void ALoansHistoryPage_AsksForTheLoan() =>
        JournalClient.PagePath(JournalFilter.None with { LoanId = Guid.Parse("0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b") }, null)
            .ShouldBe("v1/journal?loanId=0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b&limit=25");

    [Fact]
    public void Transfer_ReadsFromAndTo_WithBothAmounts()
    {
        var row = Row(Transfer(Cushion, Holiday, 431m, 99.98m));

        row.Describe(L, Polish).ShouldBe("Cushion \u2192 Holiday");
        row.SignedAmount.ShouldBeNull();
        row.FromAmount.ShouldBe(Money.From(431m, Currency.Pln));
        row.ToAmount.ShouldBe(Money.From(99.98m, Currency.Eur));
    }

    [Fact]
    public void Unallocated_IsNamedWithItsCurrency() =>
        Row(Single(JournalEntryType.Deposit, UnallocatedPln, 1m)).Describe(L, Polish).ShouldBe("Unallocated (PLN)");

    [Fact]
    public void Reversal_NamesWhatItCancels_WhenThatEntryIsLoaded()
    {
        var deposit = Single(JournalEntryType.Deposit, Cushion, 1200.50m) with { OccurredOn = new DateOnly(2026, 1, 10) };
        var reversal = Reversal(deposit);

        var row = Row(reversal, loaded: [reversal, deposit with { ReversedBy = reversal.Id }]);

        row.IsReversal.ShouldBeTrue();
        row.SignedAmount.ShouldBeNull();
        row.Describe(L, Polish).ShouldBe("Cancels the deposit of 10.01.2026 in Cushion");
    }

    [Fact]
    public void Reversal_OfAnEntryNotLoaded_SaysSo()
    {
        var reversal = Reversal(Single(JournalEntryType.Deposit, Cushion, 1m));

        Row(reversal, loaded: [reversal]).Describe(L, Polish).ShouldBe("Cancels an earlier entry");
    }

    [Fact]
    public void ReversedEntry_IsMarked_AndCannotBeReversedAgain()
    {
        var row = Row(Single(JournalEntryType.Deposit, Cushion, 1m) with { ReversedBy = Guid.CreateVersion7() });

        row.IsReversed.ShouldBeTrue();
        row.CanReverse.ShouldBeFalse();
    }

    [Fact]
    public void Entries_CanBeReversed_ButNotAReversal_NorOneTouchingAnArchivedBucket()
    {
        Row(Single(JournalEntryType.Spend, Cushion, 1m)).CanReverse.ShouldBeTrue();
        Row(Transfer(Cushion, Holiday, 1m, 1m)).CanReverse.ShouldBeTrue();
        Row(Reversal(Single(JournalEntryType.Deposit, Cushion, 1m))).CanReverse.ShouldBeFalse();
        Row(Single(JournalEntryType.Deposit, Old, 1m)).CanReverse.ShouldBeFalse();
        Row(Transfer(Cushion, Old, 1m, 1m)).CanReverse.ShouldBeFalse();
    }

    [Fact]
    public void ReversalTitle_NamesTheEntryAndItsAmount()
    {
        var spend = Single(JournalEntryType.Spend, Cushion, 75.25m) with { OccurredOn = new DateOnly(2026, 1, 12) };
        var transfer = Transfer(Cushion, Holiday, 431m, 99.98m) with { OccurredOn = new DateOnly(2026, 1, 13) };

        Row(spend).ReversalTitle(L, Polish).ShouldBe("Reverse the spend of 75,25\u00A0PLN from Cushion on 12.01.2026?");
        Row(transfer).ReversalTitle(L, Polish).ShouldBe("Reverse the transfer of 431,00\u00A0PLN from Cushion to Holiday on 13.01.2026?");
    }

    [Fact]
    public void Filter_IsActive_OnlyWithAValue_AndAPeriodMustNotEndBeforeItStarts()
    {
        JournalFilter.None.IsActive.ShouldBeFalse();
        (JournalFilter.None with { BucketId = Cushion.Id }).IsActive.ShouldBeTrue();
        (JournalFilter.None with { From = Today, To = Today }).PeriodError.ShouldBeNull();
        (JournalFilter.None with { From = Today, To = Today.AddDays(-1) }).PeriodError!.Code.ShouldBe(JournalFilter.PeriodInvalid);
    }

    [Fact]
    public void ReversalForm_HasOneId_IsDatedToday_AndChecksItsNote()
    {
        var entry = Single(JournalEntryType.Deposit, Cushion, 1m);
        var form = new ReversalForm(entry.Id, Today);

        form.Id.Version.ShouldBe(7);
        form.NoteError.ShouldBeNull();
        form.ToRequest().ShouldBe(new ReverseEntryRequest(form.Id, entry.Id, Today, null, null));
        form.ToRequest().Id.ShouldBe(form.Id);

        form.Note = "Typed the wrong amount";
        form.ToRequest().Note.ShouldBe("Typed the wrong amount");

        form.Note = " padded";
        form.NoteError!.Code.ShouldBe("input.note_invalid");
    }

    [Theory]
    [InlineData(false, false, false, null, "v1/journal?limit=25")]
    [InlineData(true, false, false, null, "v1/journal?bucketId=0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b&limit=25")]
    [InlineData(false, true, true, null, "v1/journal?from=2026-01-01&to=2026-01-15&limit=25")]
    [InlineData(false, false, false, "AQ_x-y", "v1/journal?limit=25&cursor=AQ_x-y")]
    public void PagePath_CarriesTheFilterAndTheCursor(bool bucket, bool from, bool to, string? cursor, string expected)
    {
        var filter = new JournalFilter(
            bucket ? Guid.Parse("0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b") : null,
            from ? new DateOnly(2026, 1, 1) : null,
            to ? Today : null);

        JournalClient.PagePath(filter, cursor).ShouldBe(expected);
    }

    [Fact]
    public async Task Client_GetsAPage_AndPostsAReversal()
    {
        var client = Client("{\"entries\":[],\"nextCursor\":null}");

        (await client.PageAsync(JournalFilter.None, null, Cancellation)).Value!.Entries.ShouldBeEmpty();
        await client.ReverseAsync(new ReverseEntryRequest(Guid.CreateVersion7(), Guid.CreateVersion7(), Today), Cancellation);

        asked.ShouldBe(["GET http://localhost:7071/api/v1/journal?limit=25", "POST http://localhost:7071/api/v1/journal/reversals"]);
    }

    public void Dispose() => services.Dispose();

    private static JournalRow Row(JournalEntryDto entry, IReadOnlyList<JournalEntryDto>? loaded = null) =>
        new(entry, new Dictionary<Guid, BucketDto> { [Cushion.Id] = Cushion, [Holiday.Id] = Holiday, [Old.Id] = Old, [UnallocatedPln.Id] = UnallocatedPln },
            (loaded ?? [entry]).ToDictionary(item => item.Id));

    private static JournalEntryDto Single(JournalEntryType type, BucketDto bucket, decimal amount) =>
        new(Guid.CreateVersion7(), type, Today, Recorded, null, [], null, "Manual", null,
            bucket.Id, Money.From(amount, bucket.Currency), null, null, null, null, null, null, null, null);

    private static JournalEntryDto Transfer(BucketDto from, BucketDto to, decimal sent, decimal received) =>
        new(Guid.CreateVersion7(), JournalEntryType.Transfer, Today, Recorded, null, [], null, "Manual", null,
            null, null, from.Id, to.Id, Money.From(sent, from.Currency), Money.From(received, to.Currency), null, 1m, null, null);

    // A borrow (from = lender, to = destination) or a repayment (from = source, to = lender), as the Api maps them (B18).
    private static JournalEntryDto Loan(JournalEntryType type, Guid loan, Guid? from, Guid? to, decimal amount) =>
        new(Guid.CreateVersion7(), type, Today, Recorded, null, [], null, "Manual", null,
            null, Money.From(amount, Currency.Pln), from, to, null, null, null, null, null, null, LoanId: loan);

    private static JournalEntryDto Reversal(JournalEntryDto reversed) =>
        new(Guid.CreateVersion7(), JournalEntryType.Reversal, Today, Recorded, null, [], null, "Manual", null,
            null, null, null, null, null, null, null, null, reversed.Id, null);

    private static BucketDto Bucket(string? name, Currency currency) =>
        new(Guid.CreateVersion7(), name, currency, BucketKind.Savings, null, BucketStatus.Active, BucketSystemRole.None);

    private JournalClient Client(string answer) =>
        new(new ApiClient(new HttpClient(new Handler(request =>
        {
            asked.Add($"{request.Method} {request.RequestUri}");
            return request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NoContent);
        }))
        { BaseAddress = new Uri("http://localhost:7071/api/") }));

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
