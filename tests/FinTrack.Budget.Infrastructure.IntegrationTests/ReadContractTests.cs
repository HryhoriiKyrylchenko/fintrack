using FinTrack.Budget.Application;
using FinTrack.Budget.Application.Buckets;
using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Application.Overview;
using FinTrack.Budget.Application.Settings;
using FinTrack.Budget.Contracts.V1.Journal;
using FinTrack.Budget.Contracts.V1.Overview;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FluentResults;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using BucketKind = FinTrack.Budget.Contracts.V1.Buckets.BucketKind;
using BucketSystemRole = FinTrack.Budget.Contracts.V1.Buckets.BucketSystemRole;

namespace FinTrack.Budget.Infrastructure.IntegrationTests;

/// <summary>
/// The read contracts (plan 1a step 12) against the real ledger and MongoDB: the overview's totals and
/// conversion with a known and a missing rate, and the journal read page by page in MongoDB's order.
/// Rates come from <see cref="FixedFxRates"/> (EUR to PLN only).
/// </summary>
public sealed class ReadContractTests(BudgetFixture fixture)
{
    // The fixture's clock is FakeClock.DefaultStart: 2026-01-15 in Warsaw. It does not move, so every
    // entry has the same recorded time and the ID decides the order within a day, as MongoDB sorts it.
    private static readonly DateOnly Today = new(2026, 1, 15);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Overview_TotalsEachCurrency_AndConvertsTheSubtotalOnce()
    {
        var owner = await NewOwnerAsync(Currency.Pln);
        var cushion = await CreateBucketAsync(owner, "Cushion", Currency.Pln);
        var euro = await CreateBucketAsync(owner, "Euro", Currency.Eur);
        await SendAsync<AddOpeningBalanceHandler, AddOpeningBalance>(owner, new AddOpeningBalance(Guid.CreateVersion7(), Pln(500.00m), Today.AddDays(-30), cushion, null, null, []));
        await SendAsync<AddDepositHandler, AddDeposit>(owner, new AddDeposit(Guid.CreateVersion7(), Pln(100.10m), Today.AddDays(-3), null, Currency.Pln, null, []));
        await SendAsync<AddDepositHandler, AddDeposit>(owner, new AddDeposit(Guid.CreateVersion7(), Eur(10.00m), Today.AddDays(-3), euro, null, null, []));
        await SendAsync<AddDepositHandler, AddDeposit>(owner, new AddDeposit(Guid.CreateVersion7(), Eur(2.35m), Today.AddDays(-2), null, Currency.Eur, null, []));
        await SendAsync<AddTransferHandler, AddTransfer>(owner, new AddTransfer(Guid.CreateVersion7(), Pln(43.00m), Eur(10.00m), null, Today, cushion, null, euro, null, null, []));

        var overview = (await OverviewAsync(owner)).Value;

        // PLN: 500.00 + 100.10 − 43.00; EUR: 10.00 + 2.35 + 10.00 = 22.35 × 4.2735 = 95.512725 → 95.51.
        overview.ReportingCurrency.ShouldBe(Currency.Pln);
        overview.AsOf.ShouldBe(Today);
        overview.Totals.ShouldBe(
        [
            new CurrencyTotalDto(Currency.Pln, Pln(557.10m), null),
            new CurrencyTotalDto(Currency.Eur, Eur(22.35m), new ConversionDto(Pln(95.51m), FixedFxRates.EurToPln.Rate, FixedFxRates.EurToPln.Date)),
        ]);
        overview.ReportingTotal.ShouldBe(new ReportingTotalDto(Pln(652.61m), FixedFxRates.EurToPln.Date, null));

        overview.Buckets.Count.ShouldBe(4);
        overview.Buckets.Single(bucket => bucket.Id == cushion).ShouldBe(
            new BucketSummaryDto(cushion, "Cushion", Currency.Pln, BucketKind.Savings, null, BucketSystemRole.None, Pln(457.00m), Pln(457.00m), Pln(0m)));
        overview.Buckets.Single(bucket => bucket.Id == euro).Available.ShouldBe(Eur(20.00m));
        overview.Buckets.Where(bucket => bucket.SystemRole == BucketSystemRole.Unallocated)
            .Select(bucket => bucket.Available).ShouldBe([Pln(100.10m), Eur(2.35m)], ignoreOrder: true);
    }

    [Fact]
    public async Task Overview_WithAMissingRate_LeavesTheConvertedTotalOut_AndKeepsTheRest()
    {
        var owner = await NewOwnerAsync(Currency.Pln);
        var dollars = await CreateBucketAsync(owner, "Dollars", Currency.Usd);
        await SendAsync<AddDepositHandler, AddDeposit>(owner, new AddDeposit(Guid.CreateVersion7(), Pln(10.00m), Today, null, Currency.Pln, null, []));
        await SendAsync<AddDepositHandler, AddDeposit>(owner, new AddDeposit(Guid.CreateVersion7(), Usd(7.00m), Today, dollars, null, null, []));

        var overview = (await OverviewAsync(owner)).Value;

        overview.ReportingTotal.ShouldBe(new ReportingTotalDto(null, null, "fx.rate_unavailable"));
        overview.Totals.ShouldBe([new CurrencyTotalDto(Currency.Pln, Pln(10.00m), null), new CurrencyTotalDto(Currency.Usd, Usd(7.00m), null)]);
        overview.Buckets.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Journal_ReadPageByPage_IsEveryEntryOnce_NewestFirst_InMongoDbsOrder()
    {
        var owner = await NewOwnerAsync(Currency.Pln);
        var cushion = await CreateBucketAsync(owner, "Cushion", Currency.Pln);
        var holiday = await CreateBucketAsync(owner, "Holiday", Currency.Pln);
        await SendAsync<AddOpeningBalanceHandler, AddOpeningBalance>(owner, new AddOpeningBalance(Guid.CreateVersion7(), Pln(500m), Today.AddDays(-30), cushion, null, null, []));

        // Several entries on one day with one recorded time: only the IDs order them.
        for (var count = 0; count < 5; count++)
        {
            await SendAsync<AddDepositHandler, AddDeposit>(owner, new AddDeposit(Guid.CreateVersion7(), Pln(1m + count), Today.AddDays(-2), cushion, null, null, []));
        }

        var transfer = Guid.CreateVersion7();
        await SendAsync<AddTransferHandler, AddTransfer>(owner, new AddTransfer(transfer, Pln(20m), Pln(20m), null, Today.AddDays(-1), cushion, null, holiday, null, null, []));
        var reversal = Guid.CreateVersion7();
        await SendAsync<ReverseEntryHandler, ReverseEntry>(owner, new ReverseEntry(reversal, transfer, Today, "Wrong bucket", []));
        var stored = (await StateAsync(owner)).Journal;
        stored.Count.ShouldBe(8);

        var read = new List<JournalEntryDto>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = (await JournalAsync(owner, new GetJournal(Limit: 3, Cursor: cursor))).Value;
            read.AddRange(page.Entries);
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null);

        pages.ShouldBe(3);
        read.Select(entry => entry.Id).ShouldBe(ReplayOrder.Sort(stored).Reverse().Select(entry => entry.Id));
        read[0].ShouldSatisfyAllConditions(
            entry => entry.Type.ShouldBe(JournalEntryType.Reversal),
            entry => entry.ReversedEntryId.ShouldBe(transfer),
            entry => entry.Note.ShouldBe("Wrong bucket"));
        read[1].ShouldSatisfyAllConditions(
            entry => entry.Id.ShouldBe(transfer),
            entry => entry.ReversedBy.ShouldBe(reversal),
            entry => entry.Rate.ShouldBe(1m));
        read[^1].Type.ShouldBe(JournalEntryType.OpeningBalance);
    }

    [Fact]
    public async Task Journal_Filters_ByBucketAndPeriod()
    {
        var owner = await NewOwnerAsync(Currency.Pln);
        var cushion = await CreateBucketAsync(owner, "Cushion", Currency.Pln);
        var holiday = await CreateBucketAsync(owner, "Holiday", Currency.Pln);
        var opening = Guid.CreateVersion7();
        var transfer = Guid.CreateVersion7();
        var holidayDeposit = Guid.CreateVersion7();
        await SendAsync<AddOpeningBalanceHandler, AddOpeningBalance>(owner, new AddOpeningBalance(opening, Pln(500m), Today.AddDays(-30), cushion, null, null, []));
        await SendAsync<AddTransferHandler, AddTransfer>(owner, new AddTransfer(transfer, Pln(20m), Pln(20m), null, Today.AddDays(-10), cushion, null, holiday, null, null, []));
        await SendAsync<AddDepositHandler, AddDeposit>(owner, new AddDeposit(holidayDeposit, Pln(5m), Today.AddDays(-5), holiday, null, null, []));

        (await IdsAsync(owner, new GetJournal(BucketId: cushion))).ShouldBe([transfer, opening]);
        (await IdsAsync(owner, new GetJournal(BucketId: holiday))).ShouldBe([holidayDeposit, transfer]);
        (await IdsAsync(owner, new GetJournal(From: Today.AddDays(-10), To: Today.AddDays(-5)))).ShouldBe([holidayDeposit, transfer]);
        (await IdsAsync(owner, new GetJournal(BucketId: cushion, From: Today.AddDays(-9)))).ShouldBeEmpty();
    }

    [Fact]
    public void ContractEntryTypes_AreTheStoredTypeNames() =>
        Enum.GetNames<JournalEntryType>().ShouldBe(BudgetMongoMapping.JournalEntryTypes.Values, ignoreOrder: true);

    private async Task<OwnerId> NewOwnerAsync(Currency reporting)
    {
        var owner = BudgetFixture.NewOwner();
        (await SendAsync<SaveOwnerSettingsHandler, SaveOwnerSettings>(owner, new SaveOwnerSettings(reporting, "Europe/Warsaw", "pl-PL"))).IsSuccess.ShouldBeTrue();
        return owner;
    }

    private async Task<Guid> CreateBucketAsync(OwnerId owner, string name, Currency currency)
    {
        var id = Guid.CreateVersion7();
        (await SendAsync<CreateBucketHandler, CreateBucket>(owner, new CreateBucket(id, name, currency, BucketKind.Savings, null))).IsSuccess.ShouldBeTrue();
        return id;
    }

    private async Task<Result> SendAsync<THandler, TCommand>(OwnerId owner, TCommand command)
        where THandler : ICommandHandler<TCommand, Result>
        where TCommand : ICommand<Result>
    {
        await using var scope = fixture.ScopeFor(owner);
        var result = await ActivatorUtilities.CreateInstance<THandler>(scope.ServiceProvider).Handle(command, Cancellation);
        result.IsSuccess.ShouldBeTrue(string.Join(", ", result.Errors.Select(error => error.Message)));
        return result;
    }

    private async Task<Result<BudgetOverviewDto>> OverviewAsync(OwnerId owner)
    {
        await using var scope = fixture.ScopeFor(owner);
        return await ActivatorUtilities.CreateInstance<GetBudgetOverviewHandler>(scope.ServiceProvider).Handle(new GetBudgetOverview(), Cancellation);
    }

    private async Task<Result<JournalPageDto>> JournalAsync(OwnerId owner, GetJournal query)
    {
        await using var scope = fixture.ScopeFor(owner);
        return await ActivatorUtilities.CreateInstance<GetJournalHandler>(scope.ServiceProvider).Handle(query, Cancellation);
    }

    private async Task<IReadOnlyList<Guid>> IdsAsync(OwnerId owner, GetJournal query) =>
        [.. (await JournalAsync(owner, query)).Value.Entries.Select(entry => entry.Id)];

    private async Task<BudgetSnapshot> StateAsync(OwnerId owner)
    {
        await using var scope = fixture.ScopeFor(owner);
        return (await scope.ServiceProvider.GetRequiredService<BudgetStateLoader>().LoadAsync(Cancellation)).Value;
    }

    private static Money Pln(decimal amount) => Money.From(amount, Currency.Pln);

    private static Money Eur(decimal amount) => Money.From(amount, Currency.Eur);

    private static Money Usd(decimal amount) => Money.From(amount, Currency.Usd);
}
