using FinTrack.Shared.Infrastructure.Ledger;
using FinTrack.Shared.Infrastructure.Owners;
using FinTrack.Shared.Kernel.Events;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Persistence;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Shared.Kernel.Time;
using FluentResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.Outbox;

/// <summary>
/// Delivers outbox messages to their handlers: right after the commit that stored them
/// (<see cref="DispatchAsync"/>) and later through the sweep timer (<see cref="DispatchPendingAsync"/>),
/// which picks up anything left undelivered. Delivery is at least once; handlers are idempotent
/// through the handled-event record, so the effect is exactly once (invariant 13).
/// </summary>
/// <remarks>
/// <para>
/// Each message is handled in its own DI scope whose <see cref="IOwnerContext"/> is the message's
/// owner, so a handler reads and writes only that owner's data. The sweep is the one documented
/// exception to owner scoping (ARCHITECTURE 2.3): it reads pending messages of all owners.
/// </para>
/// <para>
/// When every handler succeeds the message gets <see cref="OutboxMessage.DispatchedAt"/>; otherwise
/// <see cref="OutboxMessage.Attempts"/> increases, <see cref="OutboxMessage.LastErrorCode"/> is set and
/// the message stays pending for the next sweep. Delivery never throws to its caller (except on
/// cancellation), so a failing handler never fails the command that produced the event. Logs carry
/// identifiers, event names and codes only.
/// </para>
/// </remarks>
public sealed partial class OutboxDispatcher
{
    /// <summary>How many pending messages the sweep reads at a time.</summary>
    public const int BatchSize = 100;

    /// <summary>Error code stored when a handler throws.</summary>
    public const string HandlerException = "outbox.handler_exception";

    /// <summary>Error code stored when no event type has the message's name.</summary>
    public const string UnknownEvent = "outbox.unknown_event";

    /// <summary>Error code stored when a handler fails without an <see cref="AppError"/>.</summary>
    public const string HandlerFailed = "outbox.handler_failed";

    private readonly IServiceScopeFactory scopes;
    private readonly IMongoDatabase database;
    private readonly LedgerCollections ledgers;
    private readonly IntegrationEventRegistry events;
    private readonly IClock clock;
    private readonly ILogger<OutboxDispatcher> logger;

    /// <summary>Creates the dispatcher.</summary>
    public OutboxDispatcher(
        IServiceScopeFactory scopes,
        IMongoDatabase database,
        LedgerCollections ledgers,
        IntegrationEventRegistry events,
        IClock clock,
        ILogger<OutboxDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(ledgers);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        this.scopes = scopes;
        this.database = database;
        this.ledgers = ledgers;
        this.events = events;
        this.clock = clock;
        this.logger = logger;
    }

    /// <summary>Delivers the just-committed events <paramref name="eventIds"/> of <paramref name="ledger"/>.</summary>
    public async Task DispatchAsync(LedgerName ledger, IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eventIds);

        var outbox = Outbox(ledger);
        var filter = Builders<OutboxMessage>.Filter.In(message => message.Id, eventIds)
            & Builders<OutboxMessage>.Filter.Eq(message => message.DispatchedAt, null);

        List<OutboxMessage> messages;
        try
        {
            messages = await outbox.Find(filter).SortBy(message => message.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The sweep delivers them later; the commit has already succeeded.
            LogReadFailed(exception, ledger.Value);
            return;
        }

        foreach (var message in messages)
        {
            await DispatchMessageAsync(outbox, message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Delivers every pending message of every ledger, oldest first, each at most once per call.
    /// Returns how many were delivered.
    /// </summary>
    public async Task<int> DispatchPendingAsync(CancellationToken cancellationToken)
    {
        var delivered = 0;

        foreach (var ledger in ledgers.Ledgers)
        {
            var outbox = Outbox(ledger);
            Guid? after = null;

            while (true)
            {
                // Not filtered by owner: the documented exception (ARCHITECTURE 2.3).
                var filter = Builders<OutboxMessage>.Filter.Eq(message => message.DispatchedAt, null);
                if (after is { } last)
                {
                    filter &= Builders<OutboxMessage>.Filter.Gt(message => message.Id, last);
                }

                var batch = await outbox.Find(filter)
                    .SortBy(message => message.Id)
                    .Limit(BatchSize)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                foreach (var message in batch)
                {
                    if (await DispatchMessageAsync(outbox, message, cancellationToken).ConfigureAwait(false))
                    {
                        delivered++;
                    }
                }

                if (batch.Count < BatchSize)
                {
                    break;
                }

                after = batch[^1].Id;
            }
        }

        return delivered;
    }

    private async Task<bool> DispatchMessageAsync(IMongoCollection<OutboxMessage> outbox, OutboxMessage message, CancellationToken cancellationToken)
    {
        string? errorCode;
        try
        {
            errorCode = await HandleAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogHandlerException(exception, message.Id, message.EventName);
            errorCode = HandlerException;
        }

        try
        {
            if (errorCode is null)
            {
                await outbox.UpdateOneAsync(
                    ThisMessage(message),
                    Builders<OutboxMessage>.Update.Set(stored => stored.DispatchedAt, clock.UtcNow),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                LogDispatched(message.Id, message.EventName);
                return true;
            }

            await outbox.UpdateOneAsync(
                ThisMessage(message),
                Builders<OutboxMessage>.Update.Inc(stored => stored.Attempts, 1).Set(stored => stored.LastErrorCode, errorCode),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            LogFailed(message.Id, message.EventName, errorCode, message.Attempts + 1);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The message stays as it was; the next sweep delivers it again (handlers are idempotent).
            LogBookkeepingFailed(exception, message.Id);
            return false;
        }
    }

    /// <summary>Runs the handlers under the message's owner; returns <c>null</c> on success, otherwise the error code.</summary>
    private async Task<string?> HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (events.Find(message.EventName) is not { } registration)
        {
            return UnknownEvent;
        }

        var integrationEvent = (IIntegrationEvent)BsonSerializer.Deserialize(message.Payload, registration.EventType);

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedOwnerContext>().Set(message.OwnerId);
        if (scope.ServiceProvider.GetRequiredService<IOwnerContext>().OwnerId != message.OwnerId)
        {
            throw new InvalidOperationException("IOwnerContext must resolve to the ScopedOwnerContext so handlers run under the message's owner.");
        }

        var result = await registration.Handle(scope.ServiceProvider, integrationEvent, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? null : CodeOf(result);
    }

    private static string CodeOf(Result result) =>
        result.Errors.OfType<AppError>().FirstOrDefault()?.Code ?? HandlerFailed;

    private static FilterDefinition<OutboxMessage> ThisMessage(OutboxMessage message) =>
        Builders<OutboxMessage>.Filter.Eq(stored => stored.Id, message.Id)
        & Builders<OutboxMessage>.Filter.Eq(stored => stored.OwnerId, message.OwnerId);

    private IMongoCollection<OutboxMessage> Outbox(LedgerName ledger) =>
        database.GetCollection<OutboxMessage>(LedgerCollections.OutboxCollection(ledger));

    [LoggerMessage(Level = LogLevel.Debug, Message = "Outbox message {MessageId} ({EventName}) dispatched.")]
    private partial void LogDispatched(Guid messageId, string eventName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({EventName}) failed with {ErrorCode}; attempts so far: {Attempts}.")]
    private partial void LogFailed(Guid messageId, string eventName, string errorCode, int attempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "A handler of outbox message {MessageId} ({EventName}) threw.")]
    private partial void LogHandlerException(Exception exception, Guid messageId, string eventName);

    [LoggerMessage(Level = LogLevel.Error, Message = "The delivery state of outbox message {MessageId} could not be saved.")]
    private partial void LogBookkeepingFailed(Exception exception, Guid messageId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Committed outbox messages of ledger {Ledger} could not be read for dispatch; the sweep will deliver them.")]
    private partial void LogReadFailed(Exception exception, string ledger);
}
