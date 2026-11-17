using FinTrack.Budget.Application;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace FinTrack.Budget.Infrastructure.IntegrationTests.Journal;

/// <summary>
/// The stored format of journal entries in <c>budget_journal</c>, element by element. The journal lives
/// forever (ARCHITECTURE 2.2): a test that fails here means a change that needs an upcaster.
/// </summary>
public sealed class JournalStorageTests(BudgetFixture fixture)
{
    private static readonly DateOnly Day = new(2026, 1, 15);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deposit_HasThePinnedShape()
    {
        var owner = BudgetFixture.NewOwner();
        var bucket = BucketId.From(Guid.CreateVersion7());
        var deposit = NewDeposit(owner, bucket, 250.00m, Day, new EntryInfo(null, [], EntrySource.Manual));
        await AppendAsync(owner, deposit);

        (await RawAsync(deposit.Id)).ShouldBe(new BsonDocument
        {
            { "_id", Uuid(deposit.Id) },
            { "type", "Deposit" },
            { "ownerId", Uuid(owner.Value) },
            { "schemaVersion", 1 },
            { "recordedAt", new BsonDateTime(FakeClock.DefaultStart.UtcDateTime) },
            { "occurredOn", "2026-01-15" },
            { "tags", new BsonArray() },
            { "source", "Manual" },
            { "bucketId", Uuid(bucket.Value) },
            { "amount", new BsonDocument { { "amount", new BsonDecimal128(250.00m) }, { "currency", "PLN" } } },
        });
    }

    [Theory]
    [InlineData("OpeningBalance")]
    [InlineData("Spend")]
    public async Task OtherSingleBucketEntries_HaveTheDepositsShape_WithTheirOwnType(string type)
    {
        var owner = BudgetFixture.NewOwner();
        var bucket = BucketId.From(Guid.CreateVersion7());
        var id = JournalEntryId.From(Guid.CreateVersion7());
        JournalEntry entry = type == "Spend"
            ? Spend.Create(id, owner, bucket, Money.From(12.30m, Currency.Pln), Day, Day, EntryInfo.None, FakeClock.DefaultStart).Value
            : OpeningBalance.Create(id, owner, bucket, Money.From(12.30m, Currency.Pln), Day, Day, EntryInfo.None, FakeClock.DefaultStart).Value;
        await AppendAsync(owner, entry);

        (await RawAsync(entry.Id)).ShouldBe(new BsonDocument
        {
            { "_id", Uuid(entry.Id) },
            { "type", type },
            { "ownerId", Uuid(owner.Value) },
            { "schemaVersion", 1 },
            { "recordedAt", new BsonDateTime(FakeClock.DefaultStart.UtcDateTime) },
            { "occurredOn", "2026-01-15" },
            { "tags", new BsonArray() },
            { "source", "Manual" },
            { "bucketId", Uuid(bucket.Value) },
            { "amount", new BsonDocument { { "amount", new BsonDecimal128(12.30m) }, { "currency", "PLN" } } },
        });

        (await ReadAsync(owner)).ShouldHaveSingleItem().GetType().Name.ShouldBe(type);
    }

    [Fact]
    public async Task Transfer_HasThePinnedShape_WithFeeAndRate()
    {
        var owner = BudgetFixture.NewOwner();
        var from = BucketId.From(Guid.CreateVersion7());
        var to = BucketId.From(Guid.CreateVersion7());
        var transfer = Transfer.Create(JournalEntryId.From(Guid.CreateVersion7()), owner, from, to, Money.From(1000.00m, Currency.Pln), Money.From(232.65m, Currency.Eur),
            Money.From(10.00m, Currency.Pln), Day, Day, EntryInfo.None, FakeClock.DefaultStart).Value;
        await AppendAsync(owner, transfer);

        (await RawAsync(transfer.Id)).ShouldBe(new BsonDocument
        {
            { "_id", Uuid(transfer.Id) },
            { "type", "Transfer" },
            { "ownerId", Uuid(owner.Value) },
            { "schemaVersion", 1 },
            { "recordedAt", new BsonDateTime(FakeClock.DefaultStart.UtcDateTime) },
            { "occurredOn", "2026-01-15" },
            { "tags", new BsonArray() },
            { "source", "Manual" },
            { "fromBucketId", Uuid(from.Value) },
            { "toBucketId", Uuid(to.Value) },
            { "fromAmount", new BsonDocument { { "amount", new BsonDecimal128(1000.00m) }, { "currency", "PLN" } } },
            { "toAmount", new BsonDocument { { "amount", new BsonDecimal128(232.65m) }, { "currency", "EUR" } } },
            { "fee", new BsonDocument { { "amount", new BsonDecimal128(10.00m) }, { "currency", "PLN" } } },
            { "rate", new BsonDecimal128(0.235m) },
        });

        var read = (await ReadAsync(owner)).ShouldHaveSingleItem().ShouldBeOfType<Transfer>();
        read.HasSameContent(transfer).ShouldBeTrue();
        read.Rate.ShouldBe(0.235m);
    }

    [Fact]
    public async Task Transfer_WithoutFee_HasNoFeeElement()
    {
        var owner = BudgetFixture.NewOwner();
        var transfer = Transfer.Create(JournalEntryId.From(Guid.CreateVersion7()), owner, BucketId.From(Guid.CreateVersion7()), BucketId.From(Guid.CreateVersion7()),
            Money.From(5m, Currency.Pln), Money.From(5m, Currency.Pln), null, Day, Day, EntryInfo.None, FakeClock.DefaultStart).Value;
        await AppendAsync(owner, transfer);

        (await RawAsync(transfer.Id)).Contains("fee").ShouldBeFalse();
        (await ReadAsync(owner)).ShouldHaveSingleItem().ShouldBeOfType<Transfer>().Fee.ShouldBeNull();
    }

    [Fact]
    public async Task Deposit_WithNoteAndTags_StoresThem_AndKeepsTheAmountsScale()
    {
        var owner = BudgetFixture.NewOwner();
        var deposit = NewDeposit(owner, BucketId.From(Guid.CreateVersion7()), 1.10m, Day, new EntryInfo("Salary\nJanuary", ["work", "monthly"], EntrySource.Manual));
        await AppendAsync(owner, deposit);

        var stored = await RawAsync(deposit.Id);

        stored["note"].ShouldBe(new BsonString("Salary\nJanuary"));
        stored["tags"].ShouldBe(new BsonArray { "work", "monthly" });
        stored["amount"]["amount"].AsDecimal128.ToString().ShouldBe("1.10");
    }

    [Fact]
    public async Task StoredDeposit_ReadsBackWithTheSameContent()
    {
        var owner = BudgetFixture.NewOwner();
        var deposit = NewDeposit(owner, BucketId.From(Guid.CreateVersion7()), 99.99m, Day, new EntryInfo("Note", ["a"], EntrySource.Manual));
        await AppendAsync(owner, deposit);

        var read = (await ReadAsync(owner)).ShouldHaveSingleItem().ShouldBeOfType<Deposit>();

        read.HasSameContent(deposit).ShouldBeTrue();
        read.RecordedAt.ShouldBe(deposit.RecordedAt);
        read.Amount.ShouldBe(deposit.Amount);
        read.Amount.Amount.Scale.ShouldBe(deposit.Amount.Amount.Scale);
    }

    [Fact]
    public void EveryEntryType_HasAnExplicitStableName()
    {
        BudgetMongoMapping.Register();

        var entryTypes = typeof(JournalEntry).Assembly.GetTypes()
            .Where(type => type.IsSubclassOf(typeof(JournalEntry)) && !type.IsAbstract)
            .ToList();

        entryTypes.ShouldNotBeEmpty();
        foreach (var type in entryTypes)
        {
            BudgetMongoMapping.JournalEntryTypes.ShouldContainKey(type, $"{type.Name} needs a stable stored name in BudgetMongoMapping.JournalEntryTypes.");
            BsonClassMap.IsClassMapRegistered(type).ShouldBeTrue($"{type.Name} needs a class map.");
            BsonClassMap.LookupClassMap(type).Discriminator.ShouldBe(BudgetMongoMapping.JournalEntryTypes[type]);
        }

        BudgetMongoMapping.JournalEntryTypes.Values.ShouldBeUnique();
    }

    [Theory]
    [InlineData("an unknown type", "type", "Withdrawal")]
    [InlineData("a type in another case", "type", "deposit")]
    [InlineData("an unknown element", "category", "food")]
    [InlineData("an unknown source", "source", "manual")]
    [InlineData("a date in another format", "occurredOn", "15.01.2026")]
    public async Task StoredEntry_ThatDoesNotFitTheFormat_FailsToRead(string reason, string element, string value)
    {
        var owner = BudgetFixture.NewOwner();
        var document = ValidDocument(owner);
        document[element] = value;
        await fixture.Raw(BudgetCollections.Journal).InsertOneAsync(document, cancellationToken: Cancellation);

        await Should.ThrowAsync<Exception>(() => ReadAsync(owner), reason);
    }

    [Fact]
    public async Task StoredAmount_AsADouble_FailsToRead()
    {
        var owner = BudgetFixture.NewOwner();
        var document = ValidDocument(owner);
        document["amount"] = new BsonDocument { { "amount", 10.5 }, { "currency", "PLN" } };
        await fixture.Raw(BudgetCollections.Journal).InsertOneAsync(document, cancellationToken: Cancellation);

        await Should.ThrowAsync<FormatException>(() => ReadAsync(owner));
    }

    [Theory]
    [InlineData("type")]
    [InlineData("amount")]
    [InlineData("tags")]
    [InlineData("source")]
    public async Task StoredEntry_WithoutARequiredElement_FailsToRead(string element)
    {
        var owner = BudgetFixture.NewOwner();
        var document = ValidDocument(owner);
        document.Remove(element);
        await fixture.Raw(BudgetCollections.Journal).InsertOneAsync(document, cancellationToken: Cancellation);

        await Should.ThrowAsync<Exception>(() => ReadAsync(owner));
    }

    [Fact]
    public async Task Journal_IsReadInReplayOrder_AsTheDomainOrdersIt()
    {
        var owner = BudgetFixture.NewOwner();
        var bucket = BucketId.From(Guid.CreateVersion7());
        var random = new Random(20260115);

        // Few dates and few instants, so equal dates and equal instants are common and the ID decides.
        var entries = Enumerable.Range(0, 60).Select(_ => Deposit.Create(
                JournalEntryId.From(Guid.NewGuid()),
                owner,
                bucket,
                Money.From(1m, Currency.Pln),
                Day.AddDays(-random.Next(3)),
                Day,
                EntryInfo.None,
                FakeClock.DefaultStart.AddMinutes(random.Next(2))).Value)
            .ToList();
        await fixture.Raw(BudgetCollections.Journal).InsertManyAsync(entries.Select(entry => entry.ToBsonDocument<JournalEntry>()), cancellationToken: Cancellation);

        var read = await ReadAsync(owner);

        read.Select(entry => entry.Id).ShouldBe(ReplayOrder.Sort(entries).Select(entry => entry.Id));
    }

    [Fact]
    public async Task ReplayIndex_IsOnTheStoredElements()
    {
        using var cursor = await fixture.Raw(BudgetCollections.Journal).Indexes.ListAsync(Cancellation);
        var index = (await cursor.ToListAsync(Cancellation)).Single(candidate => candidate["name"] == "replay_order");

        index["key"].AsBsonDocument.ShouldBe(new BsonDocument { { "ownerId", 1 }, { "occurredOn", 1 }, { "recordedAt", 1 }, { "_id", 1 } });
    }

    [Fact]
    public async Task Journal_HasNoWayToChangeAnEntry_OnlyToAppend()
    {
        var owner = BudgetFixture.NewOwner();
        var deposit = NewDeposit(owner, BucketId.From(Guid.CreateVersion7()), 5m, Day, EntryInfo.None);
        await AppendAsync(owner, deposit);

        // The same ID again is a conflict, never a second copy or a replacement (invariants 3 and 13).
        await using var scope = fixture.ScopeFor(owner);
        var ledger = scope.ServiceProvider.GetRequiredService<ILedger>();
        var version = await ledger.GetVersionAsync(BudgetLedger.Name, Cancellation);
        var changed = NewDeposit(owner, deposit.BucketId, 6m, Day, EntryInfo.None, deposit.Id);
        (await ledger.CommitAsync(new LedgerCommit(BudgetLedger.Name, version, appends: [changed]), Cancellation)).IsFailed.ShouldBeTrue();

        (await ReadAsync(owner)).ShouldHaveSingleItem().ShouldBeOfType<Deposit>().Amount.ShouldBe(Money.From(5m, Currency.Pln));
    }

    private static Deposit NewDeposit(OwnerId owner, BucketId bucket, decimal amount, DateOnly occurredOn, EntryInfo info, Guid? id = null) =>
        Deposit.Create(JournalEntryId.From(id ?? Guid.CreateVersion7()), owner, bucket, Money.From(amount, Currency.Pln), occurredOn, Day, info, FakeClock.DefaultStart).Value;

    private async Task AppendAsync(OwnerId owner, params JournalEntry[] entries)
    {
        await using var scope = fixture.ScopeFor(owner);
        var ledger = scope.ServiceProvider.GetRequiredService<ILedger>();
        var version = await ledger.GetVersionAsync(BudgetLedger.Name, Cancellation);
        (await ledger.CommitAsync(new LedgerCommit(BudgetLedger.Name, version, appends: entries), Cancellation)).IsSuccess.ShouldBeTrue();
    }

    private async Task<IReadOnlyList<JournalEntry>> ReadAsync(OwnerId owner)
    {
        await using var scope = fixture.ScopeFor(owner);
        return await scope.ServiceProvider.GetRequiredService<IBudgetStore>().GetJournalAsync(Cancellation);
    }

    private async Task<BsonDocument> RawAsync(Guid id) =>
        await fixture.Raw(BudgetCollections.Journal).Find(new BsonDocument("_id", Uuid(id))).SingleAsync(Cancellation);

    private static BsonBinaryData Uuid(Guid value) => new(value, GuidRepresentation.Standard);

    private static BsonDocument ValidDocument(OwnerId owner) => new()
    {
        { "_id", Uuid(Guid.CreateVersion7()) },
        { "type", "Deposit" },
        { "ownerId", Uuid(owner.Value) },
        { "schemaVersion", 1 },
        { "recordedAt", new BsonDateTime(FakeClock.DefaultStart.UtcDateTime) },
        { "occurredOn", "2026-01-15" },
        { "tags", new BsonArray() },
        { "source", "Manual" },
        { "bucketId", Uuid(Guid.CreateVersion7()) },
        { "amount", new BsonDocument { { "amount", new BsonDecimal128(10m) }, { "currency", "PLN" } } },
    };
}
