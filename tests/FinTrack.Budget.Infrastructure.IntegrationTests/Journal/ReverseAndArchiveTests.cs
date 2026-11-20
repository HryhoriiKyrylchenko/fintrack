using FinTrack.Budget.Application;
using FinTrack.Budget.Application.Buckets;
using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Application.Settings;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;
using FluentResults;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using BucketKind = FinTrack.Budget.Contracts.V1.Buckets.BucketKind;

namespace FinTrack.Budget.Infrastructure.IntegrationTests.Journal;

/// <summary>
/// Reversals and archiving through the real ledger and MongoDB, and invariant 3: nothing already in the
/// journal collection is ever changed or removed, whatever the commands do.
/// </summary>
public sealed class ReverseAndArchiveTests(BudgetFixture fixture)
{
    // The fixture's clock is FakeClock.DefaultStart: 2026-01-15 in Warsaw.
    private static readonly DateOnly Today = new(2026, 1, 15);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReversingRestoresTheEarlierBalancesExactly_ThenTheEmptyBucketIsArchived()
    {
        var owner = await NewOwnerAsync();
        var cushion = await CreateBucketAsync(owner, "Cushion");
        var holiday = await CreateBucketAsync(owner, "Holiday");
        var deposit = Guid.CreateVersion7();
        var transfer = Guid.CreateVersion7();
        await SendAsync<AddDepositHandler, AddDeposit>(owner, new AddDeposit(deposit, Pln(100m), Today.AddDays(-3), cushion, null, null, []));
        var before = await StateAsync(owner);

        await SendAsync<AddTransferHandler, AddTransfer>(owner, new AddTransfer(transfer, Pln(40m), Pln(40m), null, Today, cushion, null, holiday, null, null, []));
        (await SendAsync<ReverseEntryHandler, ReverseEntry>(owner, new ReverseEntry(Guid.CreateVersion7(), transfer, Today, "Wrong bucket", []))).IsSuccess.ShouldBeTrue();

        var after = await StateAsync(owner);
        after.StateAsOf(Today).Balances.ShouldBe(before.StateAsOf(Today).Balances);

        (await SendAsync<ArchiveBucketHandler, ArchiveBucket>(owner, new ArchiveBucket(holiday))).IsSuccess.ShouldBeTrue();
        (await SendAsync<ArchiveBucketHandler, ArchiveBucket>(owner, new ArchiveBucket(cushion))).ShouldFailWith(BudgetErrorCodes.BucketNotEmpty, ErrorKind.BusinessRule);
        (await StateAsync(owner)).FindBucket(holiday)!.Status.ShouldBe(BucketStatus.Archived);

        // An archived bucket accepts no new entries.
        (await SendAsync<AddDepositHandler, AddDeposit>(owner, new AddDeposit(Guid.CreateVersion7(), Pln(1m), Today, holiday, null, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketArchived, ErrorKind.BusinessRule);
    }

    [Fact]
    public async Task UnarchivedBucket_KeepsItsHistory_AndAcceptsEntriesAndReversalsAgain()
    {
        var owner = await NewOwnerAsync();
        var holiday = await CreateBucketAsync(owner, "Holiday");
        var spend = Guid.CreateVersion7();
        await SendAsync<AddDepositHandler, AddDeposit>(owner, new AddDeposit(Guid.CreateVersion7(), Pln(50m), Today.AddDays(-5), holiday, null, null, []));
        await SendAsync<AddSpendHandler, AddSpend>(owner, new AddSpend(spend, Pln(50m), Today.AddDays(-4), holiday, null, null, []));
        (await SendAsync<ArchiveBucketHandler, ArchiveBucket>(owner, new ArchiveBucket(holiday))).IsSuccess.ShouldBeTrue();

        // Archiving freed the name; while another active bucket has it, unarchiving is refused and writes nothing.
        var other = await CreateBucketAsync(owner, "holiday");
        var before = await StateAsync(owner);
        (await SendAsync<UnarchiveBucketHandler, UnarchiveBucket>(owner, new UnarchiveBucket(holiday)))
            .ShouldFailWith(BudgetErrorCodes.BucketNameTaken, ErrorKind.BusinessRule);
        (await StateAsync(owner)).Version.ShouldBe(before.Version);

        await SendAsync<RenameBucketHandler, RenameBucket>(owner, new RenameBucket(other, "Trips"));
        (await SendAsync<UnarchiveBucketHandler, UnarchiveBucket>(owner, new UnarchiveBucket(holiday))).IsSuccess.ShouldBeTrue();
        var afterUnarchive = await StateAsync(owner);
        (await SendAsync<UnarchiveBucketHandler, UnarchiveBucket>(owner, new UnarchiveBucket(holiday))).IsSuccess.ShouldBeTrue();
        (await StateAsync(owner)).Version.ShouldBe(afterUnarchive.Version, "unarchiving an active bucket wrote something");

        var stored = await fixture.Raw(BudgetCollections.Buckets).Find(new BsonDocument("_id", new BsonBinaryData(holiday, GuidRepresentation.Standard))).SingleAsync(Cancellation);
        stored["status"].AsString.ShouldBe("Active");
        stored["name"].AsString.ShouldBe("Holiday");
        stored["version"].AsInt64.ShouldBe(3);

        // Its history is intact, an old entry can be reversed again, and new entries are accepted.
        afterUnarchive.Journal.Select(entry => entry.Id).ShouldBe(before.Journal.Select(entry => entry.Id));
        (await SendAsync<ReverseEntryHandler, ReverseEntry>(owner, new ReverseEntry(Guid.CreateVersion7(), spend, Today, "Refunded", []))).IsSuccess.ShouldBeTrue();
        (await SendAsync<AddDepositHandler, AddDeposit>(owner, new AddDeposit(Guid.CreateVersion7(), Pln(20m), Today, holiday, null, null, []))).IsSuccess.ShouldBeTrue();
        (await StateAsync(owner)).StateAsOf(Today).BalanceOf(BucketId.From(holiday)).Available.ShouldBe(Pln(70m));
    }

    [Fact]
    public async Task ReversingADepositThatWasSpent_IsRejected_AndWritesNothing()
    {
        var owner = await NewOwnerAsync();
        var cushion = await CreateBucketAsync(owner, "Cushion");
        var deposit = Guid.CreateVersion7();
        await SendAsync<AddDepositHandler, AddDeposit>(owner, new AddDeposit(deposit, Pln(100m), Today.AddDays(-3), cushion, null, null, []));
        await SendAsync<AddSpendHandler, AddSpend>(owner, new AddSpend(Guid.CreateVersion7(), Pln(70m), Today, cushion, null, null, []));
        var before = await StateAsync(owner);

        (await SendAsync<ReverseEntryHandler, ReverseEntry>(owner, new ReverseEntry(Guid.CreateVersion7(), deposit, Today, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketInsufficientFunds, ErrorKind.BusinessRule);

        (await StateAsync(owner)).Version.ShouldBe(before.Version);
    }

    [Fact]
    public async Task Journal_IsNeverChanged_OnlyAppendedTo()
    {
        var owner = await NewOwnerAsync();
        var cushion = await CreateBucketAsync(owner, "Cushion");
        var holiday = await CreateBucketAsync(owner, "Holiday");
        var spend = Guid.CreateVersion7();
        await SendAsync<AddOpeningBalanceHandler, AddOpeningBalance>(owner, new AddOpeningBalance(Guid.CreateVersion7(), Pln(500m), Today.AddDays(-30), cushion, null, null, []));
        await SendAsync<AddDepositHandler, AddDeposit>(owner, new AddDeposit(Guid.CreateVersion7(), Pln(100m), Today.AddDays(-20), cushion, null, "Salary", ["work"]));
        await SendAsync<AddSpendHandler, AddSpend>(owner, new AddSpend(spend, Pln(50m), Today.AddDays(-10), cushion, null, null, []));
        var stored = await RawJournalAsync(owner);

        // Everything the app can do to the journal: more entries, a transfer, a reversal, a rejected
        // command, a renamed and an archived bucket.
        await SendAsync<AddTransferHandler, AddTransfer>(owner, new AddTransfer(Guid.CreateVersion7(), Pln(10m), Pln(10m), null, Today, cushion, null, holiday, null, null, []));
        await SendAsync<ReverseEntryHandler, ReverseEntry>(owner, new ReverseEntry(Guid.CreateVersion7(), spend, Today, null, []));
        await SendAsync<AddSpendHandler, AddSpend>(owner, new AddSpend(Guid.CreateVersion7(), Pln(100000m), Today, cushion, null, null, []));
        await SendAsync<RenameBucketHandler, RenameBucket>(owner, new RenameBucket(cushion, "Safety net"));
        var empty = await CreateBucketAsync(owner, "Empty");
        await SendAsync<ArchiveBucketHandler, ArchiveBucket>(owner, new ArchiveBucket(empty));

        var now = await RawJournalAsync(owner);
        now.Count.ShouldBe(stored.Count + 2);
        foreach (var document in stored)
        {
            now.ShouldContain(document, "an entry was changed or removed");
        }
    }

    private async Task<OwnerId> NewOwnerAsync()
    {
        var owner = BudgetFixture.NewOwner();
        (await SendAsync<SaveOwnerSettingsHandler, SaveOwnerSettings>(owner, new SaveOwnerSettings(Currency.Pln, "Europe/Warsaw", "pl-PL"))).IsSuccess.ShouldBeTrue();
        return owner;
    }

    private async Task<Guid> CreateBucketAsync(OwnerId owner, string name)
    {
        var id = Guid.CreateVersion7();
        (await SendAsync<CreateBucketHandler, CreateBucket>(owner, new CreateBucket(id, name, Currency.Pln, BucketKind.Savings, null))).IsSuccess.ShouldBeTrue();
        return id;
    }

    private async Task<Result> SendAsync<THandler, TCommand>(OwnerId owner, TCommand command)
        where THandler : ICommandHandler<TCommand, Result>
        where TCommand : ICommand<Result>
    {
        await using var scope = fixture.ScopeFor(owner);
        return await ActivatorUtilities.CreateInstance<THandler>(scope.ServiceProvider).Handle(command, Cancellation);
    }

    private async Task<BudgetSnapshot> StateAsync(OwnerId owner)
    {
        await using var scope = fixture.ScopeFor(owner);
        return (await scope.ServiceProvider.GetRequiredService<BudgetStateLoader>().LoadAsync(Cancellation)).Value;
    }

    private async Task<List<BsonDocument>> RawJournalAsync(OwnerId owner) =>
        await fixture.Raw(BudgetCollections.Journal)
            .Find(new BsonDocument("ownerId", new BsonBinaryData(owner.Value, GuidRepresentation.Standard)))
            .ToListAsync(Cancellation);

    private static Money Pln(decimal amount) => Money.From(amount, Currency.Pln);
}
