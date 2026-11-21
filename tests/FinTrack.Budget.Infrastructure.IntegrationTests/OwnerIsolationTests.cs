using FinTrack.Budget.Application;
using FinTrack.Budget.Application.Buckets;
using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Application.Overview;
using FinTrack.Budget.Application.Settings;
using FinTrack.Budget.Contracts.V1.Buckets;
using FinTrack.Budget.Contracts.V1.Journal;
using FinTrack.Budget.Contracts.V1.Overview;
using FinTrack.Budget.Contracts.V1.Settings;
using FinTrack.Budget.Domain;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;
using FluentResults;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FinTrack.Budget.Infrastructure.IntegrationTests;

/// <summary>
/// Invariant 12 through every Budget handler: owner B never sees owner A's data, and nothing B does,
/// even with A's IDs, changes anything of A's. A's stored documents and ledger version are compared
/// byte for byte before and after.
/// </summary>
public sealed class OwnerIsolationTests(BudgetFixture fixture)
{
    // The fixture's clock is FakeClock.DefaultStart: 2026-01-15 in Warsaw.
    private static readonly DateOnly Today = new(2026, 1, 15);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnotherOwner_NeverSeesOrChangesAnything_ThroughAnyHandler()
    {
        // Owner A: settings, two buckets, every entry type and a reversal.
        var a = BudgetFixture.NewOwner();
        await SendAsync<SaveOwnerSettingsHandler, SaveOwnerSettings>(a, new SaveOwnerSettings(Currency.Pln, "Europe/Warsaw", "pl-PL"));
        var cushion = Guid.CreateVersion7();
        var holiday = Guid.CreateVersion7();
        await SendAsync<CreateBucketHandler, CreateBucket>(a, new CreateBucket(cushion, "Cushion", Currency.Pln, BucketKind.Savings, null));
        await SendAsync<CreateBucketHandler, CreateBucket>(a, new CreateBucket(holiday, "Holiday", Currency.Pln, BucketKind.Savings, null));
        var opening = Guid.CreateVersion7();
        var deposit = Guid.CreateVersion7();
        await SendAsync<AddOpeningBalanceHandler, AddOpeningBalance>(a, new AddOpeningBalance(opening, Pln(500m), Today.AddDays(-30), cushion, null, null, []));
        await SendAsync<AddDepositHandler, AddDeposit>(a, new AddDeposit(deposit, Pln(100m), Today.AddDays(-5), null, Currency.Pln, null, []));
        await SendAsync<AddSpendHandler, AddSpend>(a, new AddSpend(Guid.CreateVersion7(), Pln(50m), Today, cushion, null, null, []));
        await SendAsync<AddTransferHandler, AddTransfer>(a, new AddTransfer(Guid.CreateVersion7(), Pln(20m), Pln(20m), null, Today, cushion, null, holiday, null, null, []));
        var loan = Guid.CreateVersion7();
        (await SendAsync<AddBorrowHandler, AddBorrow>(a, new AddBorrow(loan, cushion, Pln(30m), Today.AddDays(-1), holiday, null, null, []))).IsSuccess.ShouldBeTrue();
        (await SendAsync<AddRepaymentHandler, AddRepayment>(a, new AddRepayment(Guid.CreateVersion7(), loan, Pln(10m), Today, holiday, null, null, []))).IsSuccess.ShouldBeTrue();
        (await SendAsync<ReverseEntryHandler, ReverseEntry>(a, new ReverseEntry(Guid.CreateVersion7(), opening, Today, null, []))).IsSuccess.ShouldBeFalse(
            "reversing A's opening balance would overdraw, so A's own rules reject it");
        var before = await RawAsync(a);

        // Owner B reads: nothing of A's.
        var b = BudgetFixture.NewOwner();
        (await QueryAsync<GetOwnerSettingsHandler, GetOwnerSettings, OwnerSettingsDto>(b, new GetOwnerSettings()))
            .ShouldFailWith(BudgetErrorCodes.SettingsNotFound, ErrorKind.NotFound);
        (await QueryAsync<GetBucketsHandler, GetBuckets, IReadOnlyList<BucketDto>>(b, new GetBuckets(IncludeArchived: true))).Value.ShouldBeEmpty();
        (await QueryAsync<GetBudgetOverviewHandler, GetBudgetOverview, BudgetOverviewDto>(b, new GetBudgetOverview()))
            .ShouldFailWith(BudgetErrorCodes.SettingsNotFound, ErrorKind.NotFound);
        (await QueryAsync<GetJournalHandler, GetJournal, JournalPageDto>(b, new GetJournal())).Value.Entries.ShouldBeEmpty();
        (await QueryAsync<GetJournalHandler, GetJournal, JournalPageDto>(b, new GetJournal(BucketId: cushion)))
            .ShouldFailWith(BudgetErrorCodes.BucketNotFound, ErrorKind.NotFound);
        var state = await StateAsync(b);
        state.Buckets.ShouldBeEmpty();
        state.Journal.ShouldBeEmpty();
        state.Version.ShouldBe(0);

        // Owner B writes, using A's IDs wherever a command takes one.
        await SendAsync<SaveOwnerSettingsHandler, SaveOwnerSettings>(b, new SaveOwnerSettings(Currency.Eur, "Europe/Berlin", "de-DE"));
        (await SendAsync<RenameBucketHandler, RenameBucket>(b, new RenameBucket(cushion, "Mine"))).ShouldFailWith(BudgetErrorCodes.BucketNotFound, ErrorKind.NotFound);
        (await SendAsync<ArchiveBucketHandler, ArchiveBucket>(b, new ArchiveBucket(holiday))).ShouldFailWith(BudgetErrorCodes.BucketNotFound, ErrorKind.NotFound);
        (await SendAsync<UnarchiveBucketHandler, UnarchiveBucket>(b, new UnarchiveBucket(holiday))).ShouldFailWith(BudgetErrorCodes.BucketNotFound, ErrorKind.NotFound);
        (await SendAsync<AddDepositHandler, AddDeposit>(b, new AddDeposit(Guid.CreateVersion7(), Pln(1m), Today, cushion, null, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketNotFound, ErrorKind.NotFound);
        (await SendAsync<AddSpendHandler, AddSpend>(b, new AddSpend(Guid.CreateVersion7(), Pln(1m), Today, cushion, null, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketNotFound, ErrorKind.NotFound);
        (await SendAsync<AddTransferHandler, AddTransfer>(b, new AddTransfer(Guid.CreateVersion7(), Pln(1m), Pln(1m), null, Today, cushion, null, null, Currency.Pln, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketNotFound, ErrorKind.NotFound);
        (await SendAsync<ReverseEntryHandler, ReverseEntry>(b, new ReverseEntry(Guid.CreateVersion7(), deposit, Today, null, [])))
            .ShouldFailWith(BudgetErrorCodes.EntryNotFound, ErrorKind.NotFound);
        (await SendAsync<AddBorrowHandler, AddBorrow>(b, new AddBorrow(Guid.CreateVersion7(), cushion, Pln(1m), Today, null, null, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketNotFound, ErrorKind.NotFound);
        (await SendAsync<AddRepaymentHandler, AddRepayment>(b, new AddRepayment(Guid.CreateVersion7(), loan, Pln(1m), Today, null, null, null, [])))
            .ShouldFailWith(BudgetErrorCodes.LoanNotFound, ErrorKind.NotFound);

        // A's IDs cannot be reused by B: the commit conflicts (the unique _id), it never overwrites or leaks.
        (await SendAsync<CreateBucketHandler, CreateBucket>(b, new CreateBucket(cushion, "Cushion", Currency.Pln, BucketKind.Savings, null)))
            .ShouldFailWith(ErrorCodes.ConcurrencyConflict, ErrorKind.Conflict);
        (await SendAsync<AddDepositHandler, AddDeposit>(b, new AddDeposit(deposit, Pln(100m), Today.AddDays(-5), null, Currency.Pln, null, [])))
            .ShouldFailWith(ErrorCodes.ConcurrencyConflict, ErrorKind.Conflict);

        // B's own Unallocated in A's currency is B's, not A's.
        (await SendAsync<AddDepositHandler, AddDeposit>(b, new AddDeposit(Guid.CreateVersion7(), Pln(7m), Today, null, Currency.Pln, null, []))).IsSuccess.ShouldBeTrue();

        // Nothing of A's changed: every document byte for byte, and A's ledger version.
        (await RawAsync(a)).ShouldBe(before);
        var bState = await StateAsync(b);
        bState.Buckets.ShouldHaveSingleItem().OwnerId.ShouldBe(b);
        bState.Journal.ShouldHaveSingleItem().OwnerId.ShouldBe(b);
    }

    private async Task<Result> SendAsync<THandler, TCommand>(OwnerId owner, TCommand command)
        where THandler : ICommandHandler<TCommand, Result>
        where TCommand : ICommand<Result>
    {
        await using var scope = fixture.ScopeFor(owner);
        return await ActivatorUtilities.CreateInstance<THandler>(scope.ServiceProvider).Handle(command, Cancellation);
    }

    private async Task<Result<TValue>> QueryAsync<THandler, TQuery, TValue>(OwnerId owner, TQuery query)
        where THandler : IQueryHandler<TQuery, Result<TValue>>
        where TQuery : IQuery<Result<TValue>>
    {
        await using var scope = fixture.ScopeFor(owner);
        return await ActivatorUtilities.CreateInstance<THandler>(scope.ServiceProvider).Handle(query, Cancellation);
    }

    private async Task<BudgetSnapshot> StateAsync(OwnerId owner)
    {
        await using var scope = fixture.ScopeFor(owner);
        return (await scope.ServiceProvider.GetRequiredService<BudgetStateLoader>().LoadAsync(Cancellation)).Value;
    }

    // Everything stored for the owner in Budget's collections, including its ledger head.
    private async Task<List<BsonDocument>> RawAsync(OwnerId owner)
    {
        var filter = new BsonDocument("ownerId", new BsonBinaryData(owner.Value, GuidRepresentation.Standard));
        var documents = new List<BsonDocument>();
        foreach (var collection in new[] { BudgetCollections.Settings, BudgetCollections.Buckets, BudgetCollections.Journal, "budget_ledger_heads" })
        {
            documents.AddRange(await fixture.Raw(collection).Find(filter).Sort(new BsonDocument("_id", 1)).ToListAsync(Cancellation));
        }

        return documents;
    }

    private static Money Pln(decimal amount) => Money.From(amount, Currency.Pln);
}
