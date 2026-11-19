using FinTrack.Budget.Application.Overview;
using FinTrack.Budget.Contracts.V1.Overview;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;
using Contract = FinTrack.Budget.Contracts.V1.Buckets;

namespace FinTrack.Budget.Application.Tests.Overview;

/// <summary>
/// <c>GetBudgetOverview</c> against in-memory fakes: totals per currency from the replayed balances, the
/// reporting total converted once per currency with its oldest rate date, and a missing rate that leaves
/// the converted total out with its reason while every other figure stays.
/// </summary>
public class GetBudgetOverviewHandlerTests
{
    private static readonly DateOnly Today = new(2026, 1, 15);
    private static readonly DateOnly Friday = new(2026, 1, 9);
    private static readonly DateOnly Thursday = new(2026, 1, 8);

    private readonly CallLog log = new();
    private readonly FakeLedger ledger;
    private readonly FakeBudgetStore store;
    private readonly FakeClock clock = new();
    private readonly FakeFxRateProvider rates = new();
    private int recorded;

    public GetBudgetOverviewHandlerTests()
    {
        ledger = new FakeLedger(log);
        store = new FakeBudgetStore(log) { Settings = StoredSettings.Of(Currency.Pln, "Europe/Warsaw", "pl-PL") };
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BeforeTheFirstRun_IsSettingsNotFound()
    {
        store.Settings = null;

        (await Overview()).ShouldFailWith(BudgetErrorCodes.SettingsNotFound, ErrorKind.NotFound);
    }

    [Fact]
    public async Task WithoutBuckets_TheReportingTotalIsZero_WithoutARate()
    {
        var overview = (await Overview()).Value;

        overview.ReportingCurrency.ShouldBe(Currency.Pln);
        overview.AsOf.ShouldBe(Today);
        overview.Totals.ShouldBeEmpty();
        overview.Buckets.ShouldBeEmpty();
        overview.ReportingTotal.ShouldBe(new ReportingTotalDto(Pln(0m), null, null));
        rates.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task AsOf_IsTodayInTheOwnersTimeZone()
    {
        clock.Set(new DateTimeOffset(2026, 1, 15, 23, 30, 0, TimeSpan.Zero));

        (await Overview()).Value.AsOf.ShouldBe(new DateOnly(2026, 1, 16));
    }

    [Fact]
    public async Task OneCurrency_SumsEveryActiveBucket_AndNeedsNoRate()
    {
        var cushion = Savings("Cushion", Currency.Pln);
        var holiday = Savings("Holiday", Currency.Pln);
        var unallocated = Unallocated(Currency.Pln);
        Deposit(cushion, Pln(100.10m));
        Deposit(holiday, Pln(20.05m));
        Deposit(unallocated, Pln(0.01m));

        var overview = (await Overview()).Value;

        overview.Totals.ShouldHaveSingleItem().ShouldBe(new CurrencyTotalDto(Currency.Pln, Pln(120.16m), null));
        overview.ReportingTotal.ShouldBe(new ReportingTotalDto(Pln(120.16m), null, null));
        rates.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task OtherCurrencies_AreConvertedOnce_AndTheTotalCarriesTheOldestRateDate()
    {
        var cushion = Savings("Cushion", Currency.Pln);
        var euro = Savings("Euro", Currency.Eur);
        var dollars = Savings("Dollars", Currency.Usd);
        Deposit(cushion, Pln(10.00m));
        Deposit(euro, Eur(2.00m));
        Deposit(dollars, Usd(3.00m));
        rates.With(Currency.Eur, Currency.Pln, 4.2m, Friday).With(Currency.Usd, Currency.Pln, 3.5m, Thursday);

        var overview = (await Overview()).Value;

        overview.Totals.ShouldBe(
        [
            new CurrencyTotalDto(Currency.Pln, Pln(10.00m), null),
            new CurrencyTotalDto(Currency.Eur, Eur(2.00m), new ConversionDto(Pln(8.40m), 4.2m, Friday)),
            new CurrencyTotalDto(Currency.Usd, Usd(3.00m), new ConversionDto(Pln(10.50m), 3.5m, Thursday)),
        ]);
        overview.ReportingTotal.ShouldBe(new ReportingTotalDto(Pln(28.90m), Thursday, null));
        rates.Requests.ShouldBe([(Currency.Eur, Currency.Pln), (Currency.Usd, Currency.Pln)], ignoreOrder: true);
    }

    [Fact]
    public async Task EachCurrencysSubtotal_IsConvertedOnce_NotEachBucket()
    {
        // Per bucket, 0.01 × 1.5 = 0.015 would round to 0.02 twice (0.04); the subtotal 0.02 × 1.5 = 0.03.
        Deposit(Savings("First", Currency.Eur), Eur(0.01m));
        Deposit(Savings("Second", Currency.Eur), Eur(0.01m));
        rates.With(Currency.Eur, Currency.Pln, 1.5m, Friday);

        var overview = (await Overview()).Value;

        overview.Totals.ShouldHaveSingleItem().Conversion!.Amount.ShouldBe(Pln(0.03m));
        overview.ReportingTotal.Amount.ShouldBe(Pln(0.03m));
    }

    [Fact]
    public async Task Conversion_UsesBankersRounding()
    {
        // 1.00 × 4.125 = 4.125, half-way: to the even neighbour 4.12, not 4.13.
        Deposit(Savings("Euro", Currency.Eur), Eur(1.00m));
        rates.With(Currency.Eur, Currency.Pln, 4.125m, Friday);

        (await Overview()).Value.ReportingTotal.Amount.ShouldBe(Pln(4.12m));
    }

    [Fact]
    public async Task MissingRate_LeavesTheConvertedTotalOut_WithItsReason_AndKeepsEverythingElse()
    {
        var cushion = Savings("Cushion", Currency.Pln);
        var euro = Savings("Euro", Currency.Eur);
        var dollars = Savings("Dollars", Currency.Usd);
        Deposit(cushion, Pln(10.00m));
        Deposit(euro, Eur(2.00m));
        Deposit(dollars, Usd(3.00m));
        rates.With(Currency.Eur, Currency.Pln, 4.2m, Friday);

        var overview = (await Overview()).Value;

        overview.ReportingTotal.ShouldBe(new ReportingTotalDto(null, null, "fx.rate_unavailable"));
        overview.Totals.ShouldBe(
        [
            new CurrencyTotalDto(Currency.Pln, Pln(10.00m), null),
            new CurrencyTotalDto(Currency.Eur, Eur(2.00m), new ConversionDto(Pln(8.40m), 4.2m, Friday)),
            new CurrencyTotalDto(Currency.Usd, Usd(3.00m), null),
        ]);
        overview.Buckets.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Totals_StartWithTheReportingCurrency_ThenByCode()
    {
        store.Settings = StoredSettings.Of(Currency.Usd, "Europe/Warsaw", "pl-PL");
        Savings("Zloty", Currency.Pln);
        Savings("Euro", Currency.Eur);
        Savings("Dollars", Currency.Usd);
        rates.With(Currency.Pln, Currency.Usd, 0.25m, Friday).With(Currency.Eur, Currency.Usd, 1.1m, Friday);

        var overview = (await Overview()).Value;

        overview.Totals.Select(total => total.Currency).ShouldBe([Currency.Usd, Currency.Eur, Currency.Pln]);
        overview.ReportingTotal.ShouldBe(new ReportingTotalDto(Usd(0m), Friday, null));
    }

    [Fact]
    public async Task ReversedEntries_DoNotCount()
    {
        var cushion = Savings("Cushion", Currency.Pln);
        Deposit(cushion, Pln(50m));
        var mistake = Deposit(cushion, Pln(1000m));
        store.Journal.Add(Reversal.Create(JournalEntryId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, mistake.EntryId, Today, Today, EntryInfo.None, NextRecordedAt()).Value);

        var overview = (await Overview()).Value;

        overview.Buckets.ShouldHaveSingleItem().Available.ShouldBe(Pln(50m));
        overview.ReportingTotal.Amount.ShouldBe(Pln(50m));
    }

    [Fact]
    public async Task InvestmentBuckets_CountAtTheirNetInvestedAmount_EvenBelowZero()
    {
        var unallocated = Unallocated(Currency.Usd);
        var crypto = Investment("Crypto", Currency.Usd);
        Deposit(unallocated, Usd(5m));
        Transfer(crypto, unallocated, Usd(8m));
        rates.With(Currency.Usd, Currency.Pln, 4m, Friday);

        var overview = (await Overview()).Value;

        overview.Buckets.Single(bucket => bucket.Id == crypto.Id).Available.ShouldBe(Usd(-8m));
        overview.Totals.ShouldHaveSingleItem().ShouldBe(new CurrencyTotalDto(Currency.Usd, Usd(5m), new ConversionDto(Pln(20m), 4m, Friday)));
        overview.ReportingTotal.Amount.ShouldBe(Pln(20m));
    }

    [Fact]
    public async Task ArchivedBuckets_AreLeftOut_AndTheirCurrencyNeedsNoRate()
    {
        Savings("Cushion", Currency.Pln);
        Savings("Old euro", Currency.Eur, BucketStatus.Archived);

        var overview = (await Overview()).Value;

        overview.Buckets.ShouldHaveSingleItem().Name.ShouldBe("Cushion");
        overview.Totals.ShouldHaveSingleItem().Currency.ShouldBe(Currency.Pln);
        rates.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Buckets_AreSummarisedAsCreated_WithEveryField()
    {
        // Created in reverse alphabetical order, at increasing times; the list keeps the creation order.
        var later = Savings("Zeta", Currency.Pln);
        var unallocated = Unallocated(Currency.Pln);
        var crypto = Investment("Alpha", Currency.Pln);
        Deposit(later, Pln(7.50m));
        Transfer(later, crypto, Pln(2.50m));

        var buckets = (await Overview()).Value.Buckets;

        buckets.ShouldBe(
        [
            new BucketSummaryDto(later.Id, "Zeta", Currency.Pln, Contract.BucketKind.Savings, null, Contract.BucketSystemRole.None, Pln(5.00m), Pln(5.00m), Pln(0m)),
            new BucketSummaryDto(unallocated.Id, null, Currency.Pln, Contract.BucketKind.Savings, null, Contract.BucketSystemRole.Unallocated, Pln(0m), Pln(0m), Pln(0m)),
            new BucketSummaryDto(crypto.Id, "Alpha", Currency.Pln, Contract.BucketKind.Investment, Contract.InvestmentProfile.Crypto, Contract.BucketSystemRole.None, Pln(2.50m), Pln(2.50m), Pln(0m)),
        ]);
    }

    [Fact]
    public async Task ReadsTheStateThroughTheLoader_AndWritesNothing()
    {
        Deposit(Savings("Cushion", Currency.Pln), Pln(1m));

        await Overview();

        log.Calls.ShouldBe(["version", "settings", "buckets", "journal", "version"]);
        ledger.Commits.ShouldBeEmpty();
    }

    private Task<FluentResults.Result<BudgetOverviewDto>> Overview() =>
        new GetBudgetOverviewHandler(new BudgetStateLoader(ledger, store), rates, clock).Handle(new GetBudgetOverview(), Cancellation).AsTask();

    private Bucket Savings(string name, Currency currency, BucketStatus status = BucketStatus.Active) =>
        Add(StoredBuckets.Of(Guid.CreateVersion7(), name, currency, NextCreatedAt(), status, BucketKind.Savings, null, BucketSystemRole.None));

    private Bucket Investment(string name, Currency currency) =>
        Add(StoredBuckets.Of(Guid.CreateVersion7(), name, currency, NextCreatedAt(), BucketStatus.Active, BucketKind.Investment, InvestmentProfile.Crypto, BucketSystemRole.None));

    private Bucket Unallocated(Currency currency) =>
        Add(StoredBuckets.Of(Guid.CreateVersion7(), null, currency, NextCreatedAt(), BucketStatus.Active, BucketKind.Savings, null, BucketSystemRole.Unallocated));

    private Bucket Add(Bucket bucket)
    {
        store.Buckets.Add(bucket);
        return bucket;
    }

    private Deposit Deposit(Bucket bucket, Money amount)
    {
        var deposit = Domain.Journal.Deposit.Create(JournalEntryId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, bucket.BucketId, amount,
            Today.AddDays(-5), Today, EntryInfo.None, NextRecordedAt()).Value;
        store.Journal.Add(deposit);
        return deposit;
    }

    private void Transfer(Bucket from, Bucket to, Money amount) =>
        store.Journal.Add(Domain.Journal.Transfer.Create(JournalEntryId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, from.BucketId, to.BucketId,
            amount, amount, null, Today.AddDays(-1), Today, EntryInfo.None, NextRecordedAt()).Value);

    // Distinct times, so the order of buckets and entries never depends on random ID bits.
    private DateTimeOffset NextCreatedAt() => FakeClock.DefaultStart.AddDays(-30).AddMinutes(store.Buckets.Count);

    private DateTimeOffset NextRecordedAt() => FakeClock.DefaultStart.AddDays(-5).AddSeconds(++recorded);

    private static Money Pln(decimal amount) => Money.From(amount, Currency.Pln);

    private static Money Eur(decimal amount) => Money.From(amount, Currency.Eur);

    private static Money Usd(decimal amount) => Money.From(amount, Currency.Usd);
}
