using FinTrack.Shared.Infrastructure.IntegrationTests.Mongo.Collections;
using FinTrack.Shared.Infrastructure.Ledger;
using FinTrack.Shared.Infrastructure.Mongo;
using FinTrack.Shared.Kernel.Events;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Persistence;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.IntegrationTests.Ledger;

/// <summary>
/// CLAUDE.md rule 7: journal entries, changed documents, the ledger version, outbox events and the
/// handled-event record are committed together or not at all. Each test uses its own owner, so
/// tests share the collections without seeing each other.
/// </summary>
public sealed class MongoLedgerTests : IAsyncLifetime
{
    private const string Journal = "testledger_journal";
    private const string Accounts = "testledger_accounts";

    private static readonly LedgerName TestLedger = LedgerName.From("testledger");
    private static readonly LedgerName OtherLedger = LedgerName.From("otherledger");

    private readonly MongoDatabaseFixture fixture;
    private readonly FakeClock clock = new();
    private readonly TestOwnerContext owner = new(OwnerId.From(Guid.CreateVersion7()));
    private readonly LedgerCollections collections = new(
    [
        new LedgerDocuments(TestLedger).AppendOnly<TestJournalEntry>(Journal).Mutable<TestAccount>(Accounts),
        new LedgerDocuments(OtherLedger).AppendOnly<OtherLedgerEntry>("otherledger_journal"),
    ]);

    public MongoLedgerTests(MongoDatabaseFixture fixture) => this.fixture = fixture;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        await new LedgerIndexInitializer(collections).CreateIndexesAsync(fixture.Database, Cancellation);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task GetVersion_OfAnOwnerWithoutCommits_IsZero() =>
        (await Ledger().GetVersionAsync(TestLedger, Cancellation)).ShouldBe(0);

    [Fact]
    public async Task Commit_RaisesTheVersionByOneEachTime()
    {
        var ledger = Ledger();

        for (var expected = 0L; expected < 3; expected++)
        {
            var result = await ledger.CommitAsync(Commit(expected, appends: [Entry()]), Cancellation);

            result.Value.ShouldBe(new LedgerCommitted(expected + 1, AlreadyHandled: false));
            (await ledger.GetVersionAsync(TestLedger, Cancellation)).ShouldBe(expected + 1);
        }

        (await JournalAsync()).Count.ShouldBe(3);
    }

    [Fact]
    public async Task Commit_StoresAppendsSavesAndEvents()
    {
        var entry = Entry();
        var deposit = Deposit();
        var account = Account();
        var opened = Opened(account.Id);

        var result = await Ledger().CommitAsync(Commit(0, appends: [entry, deposit], saves: [account], events: [opened]), Cancellation);

        result.IsSuccess.ShouldBeTrue();
        (await JournalAsync()).ShouldBe([entry, deposit], ignoreOrder: true);
        (await AccountsAsync()).ShouldHaveSingleItem().ShouldBe(account with { Version = 1, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow });

        var message = (await OutboxAsync()).ShouldHaveSingleItem();
        message["_id"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard).ShouldBe(opened.EventId);
        message["eventName"].ShouldBe(new BsonString("testledger.account_opened"));
        message["attempts"].ShouldBe(new BsonInt32(0));
        message["dispatchedAt"].ShouldBe(BsonNull.Value);
        message["lastErrorCode"].ShouldBe(BsonNull.Value);
        message["recordedAt"].ShouldBe(new BsonDateTime(clock.UtcNow.ToUnixTimeMilliseconds()));
        var payload = message["payload"].AsBsonDocument;
        payload["accountId"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard).ShouldBe(account.Id);
        payload["occurredAt"].ShouldBe(new BsonDateTime(opened.OccurredAt.ToUnixTimeMilliseconds()));
    }

    [Fact]
    public async Task Commit_ReplacesASavedDocumentWhoseVersionMatches()
    {
        var ledger = Ledger();
        var account = Account();
        await ledger.CommitAsync(Commit(0, saves: [account]), Cancellation);
        var stored = (await AccountsAsync()).Single();
        clock.Advance(TimeSpan.FromSeconds(1));

        var result = await ledger.CommitAsync(Commit(1, saves: [stored with { Name = "Renamed" }]), Cancellation);

        result.IsSuccess.ShouldBeTrue();
        (await AccountsAsync()).Single().ShouldBe(stored with { Name = "Renamed", Version = 2, UpdatedAt = clock.UtcNow });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(5)]
    public async Task Commit_WithAnyOtherThanTheCurrentVersion_ConflictsAndWritesNothing(long expected)
    {
        var ledger = Ledger();
        await ledger.CommitAsync(Commit(0, appends: [Entry()]), Cancellation);

        var result = await ledger.CommitAsync(Commit(expected, appends: [Entry()], saves: [Account()], events: [Opened()]), Cancellation);

        result.ShouldFailWith(ErrorCodes.ConcurrencyConflict, ErrorKind.Conflict);
        (await ledger.GetVersionAsync(TestLedger, Cancellation)).ShouldBe(1);
        (await JournalAsync()).Count.ShouldBe(1);
        (await AccountsAsync()).ShouldBeEmpty();
        (await OutboxAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Commit_WithAStaleSavedDocument_ConflictsAndWritesNothing()
    {
        var ledger = Ledger();
        var account = Account();
        await ledger.CommitAsync(Commit(0, saves: [account]), Cancellation);
        var stored = (await AccountsAsync()).Single();
        await ledger.CommitAsync(Commit(1, saves: [stored with { Name = "First change" }]), Cancellation);

        var result = await ledger.CommitAsync(Commit(2, appends: [Entry()], saves: [stored with { Name = "Stale change" }], events: [Opened()]), Cancellation);

        result.ShouldFailWith(ErrorCodes.ConcurrencyConflict, ErrorKind.Conflict);
        (await ledger.GetVersionAsync(TestLedger, Cancellation)).ShouldBe(2);
        (await JournalAsync()).ShouldBeEmpty();
        (await AccountsAsync()).Single().Name.ShouldBe("First change");
        (await OutboxAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Commit_ThatFailsWithAnExceptionMidway_WritesNothing()
    {
        var ledger = Ledger();
        // The second entry cannot be stored (an instant with an offset), after the head and the first entry were written.
        var unstorable = Entry() with { RecordedAt = new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.FromHours(1)) };

        await Should.ThrowAsync<Exception>(() => ledger.CommitAsync(Commit(0, appends: [Entry(), unstorable], events: [Opened()]), Cancellation));

        (await ledger.GetVersionAsync(TestLedger, Cancellation)).ShouldBe(0);
        (await JournalAsync()).ShouldBeEmpty();
        (await OutboxAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ConcurrentCommits_WithTheSameExpectedVersion_ExactlyOneSucceeds(long expected)
    {
        if (expected > 0)
        {
            await Ledger().CommitAsync(Commit(0, appends: [Entry()]), Cancellation);
        }

        var entries = Enumerable.Range(0, 10).Select(writer => Entry($"writer {writer}")).ToList();
        var results = await Task.WhenAll(entries.Select(entry =>
            Ledger().CommitAsync(Commit(expected, appends: [entry], events: [Opened()]), Cancellation)));

        results.Count(result => result.IsSuccess).ShouldBe(1);
        results.Where(result => result.IsFailed)
            .ShouldAllBe(result => result.Errors.Single().Message == ErrorCodes.ConcurrencyConflict);
        (await Ledger().GetVersionAsync(TestLedger, Cancellation)).ShouldBe(expected + 1);
        (await JournalAsync()).Count(entry => entry.Note.StartsWith("writer", StringComparison.Ordinal)).ShouldBe(1);
        (await OutboxAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task HandledEvent_IsRecordedOnce_AndARepeatWritesNothing()
    {
        var ledger = Ledger();
        var handled = new ProcessedEvent("testledger.on_account_opened", Guid.CreateVersion7());

        var first = await ledger.CommitAsync(Commit(0, appends: [Entry("first delivery")], handledEvent: handled), Cancellation);
        var repeat = await ledger.CommitAsync(Commit(1, appends: [Entry("second delivery")], events: [Opened()], handledEvent: handled), Cancellation);

        first.Value.ShouldBe(new LedgerCommitted(1, AlreadyHandled: false));
        repeat.Value.ShouldBe(new LedgerCommitted(1, AlreadyHandled: true));
        (await JournalAsync()).ShouldHaveSingleItem().Note.ShouldBe("first delivery");
        (await OutboxAsync()).ShouldBeEmpty();
        var record = (await ProcessedAsync()).ShouldHaveSingleItem();
        record["handlerName"].ShouldBe(new BsonString("testledger.on_account_opened"));
        record["eventId"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard).ShouldBe(handled.EventId);
    }

    [Fact]
    public async Task HandledEvent_RepeatedWithAStaleVersion_IsStillAlreadyHandled()
    {
        var ledger = Ledger();
        var handled = new ProcessedEvent("testledger.on_account_opened", Guid.CreateVersion7());
        await ledger.CommitAsync(Commit(0, handledEvent: handled), Cancellation);

        var repeat = await ledger.CommitAsync(Commit(0, handledEvent: handled), Cancellation);

        repeat.Value.ShouldBe(new LedgerCommitted(1, AlreadyHandled: true));
    }

    [Fact]
    public async Task SameEvent_HandledByTwoHandlers_IsRecordedForEach()
    {
        var ledger = Ledger();
        var eventId = Guid.CreateVersion7();

        await ledger.CommitAsync(Commit(0, handledEvent: new ProcessedEvent("testledger.first_handler", eventId)), Cancellation);
        var second = await ledger.CommitAsync(Commit(1, handledEvent: new ProcessedEvent("testledger.second_handler", eventId)), Cancellation);

        second.Value.ShouldBe(new LedgerCommitted(2, AlreadyHandled: false));
        (await ProcessedAsync()).Count.ShouldBe(2);
    }

    [Fact]
    public async Task HandledEvent_InACommitThatConflicts_IsNotRecorded()
    {
        var ledger = Ledger();
        await ledger.CommitAsync(Commit(0, appends: [Entry()]), Cancellation);
        var handled = new ProcessedEvent("testledger.on_account_opened", Guid.CreateVersion7());

        var result = await ledger.CommitAsync(Commit(0, handledEvent: handled), Cancellation);

        result.ShouldFailWith(ErrorCodes.ConcurrencyConflict, ErrorKind.Conflict);
        (await ProcessedAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task VersionsOfDifferentOwners_AreIndependent()
    {
        await Ledger().CommitAsync(Commit(0, appends: [Entry()]), Cancellation);
        var ownerOfA = owner.OwnerId;
        owner.OwnerId = OwnerId.From(Guid.CreateVersion7());

        (await Ledger().GetVersionAsync(TestLedger, Cancellation)).ShouldBe(0);
        (await Ledger().CommitAsync(Commit(0, appends: [Entry()]), Cancellation)).Value.Version.ShouldBe(1);

        owner.OwnerId = ownerOfA;
        (await Ledger().GetVersionAsync(TestLedger, Cancellation)).ShouldBe(1);
    }

    [Fact]
    public async Task DocumentOfAnotherOwner_ThrowsAndWritesNothing()
    {
        var foreign = Entry() with { OwnerId = OwnerId.From(Guid.CreateVersion7()) };

        await Should.ThrowAsync<InvalidOperationException>(() => Ledger().CommitAsync(Commit(0, appends: [Entry(), foreign]), Cancellation));

        (await Ledger().GetVersionAsync(TestLedger, Cancellation)).ShouldBe(0);
        (await JournalAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task EventOfAnotherOwner_ThrowsAndWritesNothing()
    {
        var foreign = Opened() with { OwnerId = OwnerId.From(Guid.CreateVersion7()) };

        await Should.ThrowAsync<InvalidOperationException>(() => Ledger().CommitAsync(Commit(0, appends: [Entry()], events: [foreign]), Cancellation));

        (await JournalAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task DocumentOfAnUnmappedType_Throws()
    {
        var unmapped = new UnmappedEntry { Id = Guid.CreateVersion7(), OwnerId = owner.OwnerId, RecordedAt = clock.UtcNow };

        await Should.ThrowAsync<InvalidOperationException>(() => Ledger().CommitAsync(Commit(0, appends: [unmapped]), Cancellation));
    }

    [Fact]
    public async Task DocumentOfAnotherContextsLedger_Throws()
    {
        var other = new OtherLedgerEntry { Id = Guid.CreateVersion7(), OwnerId = owner.OwnerId, RecordedAt = clock.UtcNow };

        await Should.ThrowAsync<InvalidOperationException>(() => Ledger().CommitAsync(Commit(0, appends: [other]), Cancellation));

        (await Ledger().GetVersionAsync(TestLedger, Cancellation)).ShouldBe(0);
    }

    [Fact]
    public async Task IndexInitializer_CreatesTheUniqueIndexes()
    {
        var heads = await IndexesAsync("testledger_ledger_heads");
        var processed = await IndexesAsync("testledger_processed_events");

        heads.ShouldContain(index => index["unique"].AsBoolean && index["key"].AsBsonDocument == new BsonDocument("ownerId", 1));
        processed.ShouldContain(index => index["unique"].AsBoolean
            && index["key"].AsBsonDocument == new BsonDocument { { "ownerId", 1 }, { "handlerName", 1 }, { "eventId", 1 } });

        using var names = await fixture.Database.ListCollectionNamesAsync(cancellationToken: Cancellation);
        (await names.ToListAsync(Cancellation)).ShouldContain(Journal);
    }

    private MongoLedger Ledger() => new(fixture.Client, fixture.Database, owner, clock, collections);

    private static LedgerCommit Commit(
        long expectedVersion,
        IReadOnlyList<IAppendOnlyDocument>? appends = null,
        IReadOnlyList<IMutableDocument>? saves = null,
        IReadOnlyList<IIntegrationEvent>? events = null,
        ProcessedEvent? handledEvent = null) =>
        new(TestLedger, expectedVersion, appends, saves, events, handledEvent);

    private TestJournalEntry Entry(string note = "entry") =>
        new() { Id = Guid.CreateVersion7(), OwnerId = owner.OwnerId, RecordedAt = clock.UtcNow, Note = note };

    private TestDepositEntry Deposit() =>
        new() { Id = Guid.CreateVersion7(), OwnerId = owner.OwnerId, RecordedAt = clock.UtcNow, Note = "deposit", Amount = 250.75m };

    private TestAccount Account() => new() { Id = Guid.CreateVersion7(), OwnerId = owner.OwnerId, Name = "Cushion" };

    private TestAccountOpened Opened(Guid? accountId = null) => new()
    {
        EventId = Guid.CreateVersion7(),
        OwnerId = owner.OwnerId,
        OccurredAt = clock.UtcNow,
        AccountId = accountId ?? Guid.CreateVersion7(),
    };

    private Task<IReadOnlyList<TestJournalEntry>> JournalAsync() =>
        new AppendOnlyCollection<TestJournalEntry>(fixture.Database, Journal, owner).FindAsync(_ => true, cancellationToken: Cancellation);

    private Task<IReadOnlyList<TestAccount>> AccountsAsync() =>
        new MutableCollection<TestAccount>(fixture.Database, Accounts, owner, clock).FindAsync(_ => true, cancellationToken: Cancellation);

    private Task<List<BsonDocument>> OutboxAsync() => RawOfOwnerAsync("testledger_outbox");

    private Task<List<BsonDocument>> ProcessedAsync() => RawOfOwnerAsync("testledger_processed_events");

    private Task<List<BsonDocument>> RawOfOwnerAsync(string collectionName) =>
        fixture.Database.GetCollection<BsonDocument>(collectionName)
            .Find(new BsonDocument("ownerId", new BsonBinaryData(owner.OwnerId.Value, GuidRepresentation.Standard)))
            .ToListAsync(Cancellation);

    private async Task<List<BsonDocument>> IndexesAsync(string collectionName)
    {
        using var cursor = await fixture.Database.GetCollection<BsonDocument>(collectionName).Indexes.ListAsync(Cancellation);
        return [.. (await cursor.ToListAsync(Cancellation)).Where(index => index.Contains("unique"))];
    }
}
