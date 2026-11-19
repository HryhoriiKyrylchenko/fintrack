using FinTrack.Shared.Infrastructure.IntegrationTests.Ledger;
using FinTrack.Shared.Infrastructure.Outbox;
using FinTrack.Shared.Kernel.Events;
using FinTrack.Shared.Kernel.Ids;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.IntegrationTests.Outbox;

/// <summary>
/// Events reach their handlers at least once, with effect exactly once (CLAUDE.md rule 8,
/// invariant 13), each under its own owner.
/// </summary>
public sealed class OutboxDispatcherTests(MongoDatabaseFixture fixture) : IAsyncLifetime
{
    private readonly OutboxHarness harness = new(fixture);
    private readonly OwnerId owner = OwnerId.From(Guid.CreateVersion7());

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await harness.CreateIndexesAsync(Cancellation);

    public ValueTask DisposeAsync() => harness.DisposeAsync();

    [Fact]
    public async Task CommittedEvent_IsDispatchedRightAfterTheCommit()
    {
        var opened = Opened(owner);

        (await harness.CommitAsync(owner, [opened], Cancellation)).IsSuccess.ShouldBeTrue();

        harness.Handled.Deliveries.ShouldBe([(opened.EventId, owner)]);
        (await harness.JournalOfAsync(owner, Cancellation)).ShouldHaveSingleItem().Note.ShouldBe($"handled {opened.EventId}");
        var message = await harness.MessageAsync(opened.EventId, Cancellation);
        message.DispatchedAt.ShouldBe(harness.Clock.UtcNow);
        message.Attempts.ShouldBe(0);
    }

    [Fact]
    public async Task Dispatch_DeliversOnlyTheGivenEvent_AndMarksOnlyItsMessage()
    {
        // Two pending messages of one owner: dispatching the second leaves the first to the sweep.
        var first = Opened(owner);
        var second = Opened(owner);
        await harness.CommitWithoutDispatchAsync(owner, [first], Cancellation);
        await harness.CommitWithoutDispatchAsync(owner, [second], Cancellation);

        await harness.Dispatcher.DispatchAsync(harness.Ledger, [second.EventId], Cancellation);

        harness.Handled.Deliveries.ShouldBe([(second.EventId, owner)]);
        (await harness.MessageAsync(second.EventId, Cancellation)).DispatchedAt.ShouldBe(harness.Clock.UtcNow);
        (await harness.MessageAsync(first.EventId, Cancellation)).DispatchedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Dispatch_OfAnAlreadyDispatchedEvent_DoesNothing()
    {
        var opened = Opened(owner);
        await harness.CommitAsync(owner, [opened], Cancellation);
        var dispatchedAt = harness.Clock.UtcNow;
        harness.Clock.Advance(TimeSpan.FromMinutes(1));

        await harness.Dispatcher.DispatchAsync(harness.Ledger, [opened.EventId], Cancellation);

        harness.Handled.Deliveries.ShouldHaveSingleItem();
        (await harness.MessageAsync(opened.EventId, Cancellation)).DispatchedAt.ShouldBe(dispatchedAt);
    }

    [Fact]
    public async Task Dispatch_WhenTheOutboxCannotBeRead_LeavesItToTheSweep()
    {
        // The commit has already succeeded: a failed read right after it must not turn into an error for the caller.
        using var unreachable = new MongoClient("mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=300&connectTimeoutMS=300");
        await using var offline = new OutboxHarness(fixture, unreachable.GetDatabase("fintrack_unreachable"));

        await Should.NotThrowAsync(() => offline.Dispatcher.DispatchAsync(offline.Ledger, [Guid.CreateVersion7()], Cancellation));

        offline.Logs.Lines.ShouldContain(line => line.Contains($"ledger {offline.Ledger.Value} could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UndispatchedMessage_IsDeliveredByTheSweep_Once()
    {
        var opened = Opened(owner);
        await harness.CommitWithoutDispatchAsync(owner, [opened], Cancellation);
        harness.Handled.Deliveries.ShouldBeEmpty();

        (await harness.Dispatcher.DispatchPendingAsync(Cancellation)).ShouldBe(1);
        (await harness.Dispatcher.DispatchPendingAsync(Cancellation)).ShouldBe(0);

        harness.Handled.Deliveries.ShouldBe([(opened.EventId, owner)]);
        (await harness.JournalOfAsync(owner, Cancellation)).Count.ShouldBe(1);
        (await harness.MessageAsync(opened.EventId, Cancellation)).DispatchedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task RepeatedDelivery_ChangesNothing()
    {
        var opened = Opened(owner);
        await harness.CommitAsync(owner, [opened], Cancellation);

        // As if the process stopped after the handler's commit but before the message was marked.
        await harness.Database.GetCollection<BsonDocument>(harness.OutboxCollection).UpdateOneAsync(
            new BsonDocument("_id", new BsonBinaryData(opened.EventId, GuidRepresentation.Standard)),
            new BsonDocument("$set", new BsonDocument("dispatchedAt", BsonNull.Value)),
            cancellationToken: Cancellation);
        (await harness.Dispatcher.DispatchPendingAsync(Cancellation)).ShouldBe(1);

        harness.Handled.Deliveries.Count.ShouldBe(2);
        (await harness.JournalOfAsync(owner, Cancellation)).Count.ShouldBe(1);
        (await harness.MessageAsync(opened.EventId, Cancellation)).DispatchedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task FailingHandler_LeavesTheMessagePending_AndTheCommitStands()
    {
        harness.Behaviour.FailWithCode = "test.handler_refused";
        var deposit = Deposit(owner);

        var commit = await harness.CommitAsync(owner, [deposit], Cancellation);

        commit.IsSuccess.ShouldBeTrue();
        var message = await harness.MessageAsync(deposit.EventId, Cancellation);
        message.DispatchedAt.ShouldBeNull();
        message.Attempts.ShouldBe(1);
        message.LastErrorCode.ShouldBe("test.handler_refused");

        (await harness.Dispatcher.DispatchPendingAsync(Cancellation)).ShouldBe(0);
        (await harness.MessageAsync(deposit.EventId, Cancellation)).Attempts.ShouldBe(2);

        harness.Behaviour.FailWithCode = null;
        (await harness.Dispatcher.DispatchPendingAsync(Cancellation)).ShouldBe(1);
        message = await harness.MessageAsync(deposit.EventId, Cancellation);
        message.DispatchedAt.ShouldNotBeNull();
        message.Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task ThrowingHandler_IsRecordedAsAnException_AndTheCommitStands()
    {
        harness.Behaviour.Throw = true;
        var deposit = Deposit(owner);

        (await harness.CommitAsync(owner, [deposit], Cancellation)).IsSuccess.ShouldBeTrue();

        var message = await harness.MessageAsync(deposit.EventId, Cancellation);
        message.DispatchedAt.ShouldBeNull();
        message.Attempts.ShouldBe(1);
        message.LastErrorCode.ShouldBe(OutboxDispatcher.HandlerException);
    }

    [Fact]
    public async Task UnknownEventName_IsRecordedAndStaysPending()
    {
        var id = Guid.CreateVersion7();
        await harness.Database.GetCollection<OutboxMessage>(harness.OutboxCollection).InsertOneAsync(
            new OutboxMessage
            {
                Id = id,
                OwnerId = owner,
                RecordedAt = harness.Clock.UtcNow,
                EventName = "testledger.no_such_event",
                Payload = [],
            },
            cancellationToken: Cancellation);

        (await harness.Dispatcher.DispatchPendingAsync(Cancellation)).ShouldBe(0);

        var message = await harness.MessageAsync(id, Cancellation);
        message.Attempts.ShouldBe(1);
        message.LastErrorCode.ShouldBe(OutboxDispatcher.UnknownEvent);
    }

    [Fact]
    public async Task EventWithoutHandlers_IsMarkedDispatched()
    {
        var nothing = new TestNothingHappened { EventId = Guid.CreateVersion7(), OwnerId = owner, OccurredAt = harness.Clock.UtcNow };

        await harness.CommitAsync(owner, [nothing], Cancellation);

        (await harness.MessageAsync(nothing.EventId, Cancellation)).DispatchedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Sweep_HandlesEachMessageUnderItsOwnOwner()
    {
        var otherOwner = OwnerId.From(Guid.CreateVersion7());
        var first = Opened(owner);
        var second = Opened(otherOwner);
        await harness.CommitWithoutDispatchAsync(owner, [first], Cancellation);
        await harness.CommitWithoutDispatchAsync(otherOwner, [second], Cancellation);

        (await harness.Dispatcher.DispatchPendingAsync(Cancellation)).ShouldBe(2);

        harness.Handled.Deliveries.ShouldBe([(first.EventId, owner), (second.EventId, otherOwner)], ignoreOrder: true);
        (await harness.JournalOfAsync(owner, Cancellation)).ShouldHaveSingleItem().Note.ShouldBe($"handled {first.EventId}");
        (await harness.JournalOfAsync(otherOwner, Cancellation)).ShouldHaveSingleItem().Note.ShouldBe($"handled {second.EventId}");
    }

    [Fact]
    public async Task Sweep_DeliversMoreMessagesThanOneBatch()
    {
        var events = Enumerable.Range(0, OutboxDispatcher.BatchSize + 5).Select(_ => (IIntegrationEvent)Opened(owner)).ToList();
        await harness.CommitWithoutDispatchAsync(owner, events, Cancellation);

        (await harness.Dispatcher.DispatchPendingAsync(Cancellation)).ShouldBe(events.Count);
    }

    [Fact]
    public async Task Sweep_TriesAFailingMessageOncePerCall_AcrossBatches()
    {
        // The failing message is on the first page (its ID is the oldest); the next pages must not bring it back.
        harness.Behaviour.FailWithCode = "test.handler_refused";
        var failing = Deposit(owner) with { EventId = Guid.CreateVersion7(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)) };
        var events = Enumerable.Range(0, OutboxDispatcher.BatchSize + 5).Select(_ => (IIntegrationEvent)Opened(owner)).ToList();
        await harness.CommitWithoutDispatchAsync(owner, [failing, .. events], Cancellation);

        (await harness.Dispatcher.DispatchPendingAsync(Cancellation)).ShouldBe(events.Count);

        (await harness.MessageAsync(failing.EventId, Cancellation)).Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task Logs_ContainIdsNamesAndCodes_NeverEventData()
    {
        harness.Behaviour.FailWithCode = "test.handler_refused";
        var deposit = Deposit(owner) with { Amount = 98765.43m };

        await harness.CommitAsync(owner, [deposit], Cancellation);

        harness.Logs.Lines.ShouldContain(line => line.Contains(deposit.EventId.ToString(), StringComparison.Ordinal)
            && line.Contains("test.handler_refused", StringComparison.Ordinal)
            && line.Contains("testledger.deposit_recorded", StringComparison.Ordinal));
        harness.Logs.Lines.ShouldAllBe(line => !line.Contains("98765", StringComparison.Ordinal));
    }

    private TestAccountOpened Opened(OwnerId eventOwner) => new()
    {
        EventId = Guid.CreateVersion7(),
        OwnerId = eventOwner,
        OccurredAt = harness.Clock.UtcNow,
        AccountId = Guid.CreateVersion7(),
    };

    private TestDepositRecorded Deposit(OwnerId eventOwner) => new()
    {
        EventId = Guid.CreateVersion7(),
        OwnerId = eventOwner,
        OccurredAt = harness.Clock.UtcNow,
        Amount = 250.75m,
    };
}
