using FinTrack.Shared.Infrastructure.Mongo;
using FinTrack.Shared.Infrastructure.Outbox;
using FinTrack.Shared.Kernel.Events;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Persistence;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Shared.Kernel.Time;
using FluentResults;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.Ledger;

/// <summary>
/// <see cref="ILedger"/> on MongoDB: every commit is one multi-document transaction (CLAUDE.md
/// rule 7). Transactions need a replica set, which is why even the local server runs as one.
/// </summary>
/// <remarks>
/// <para>Order inside the transaction:</para>
/// <list type="number">
/// <item><description>the handled-event record, if any: already present means already handled, so the transaction is aborted and nothing is written;</description></item>
/// <item><description>the <see cref="LedgerHead"/>: created when the expected version is 0, otherwise replaced only if its version equals the expected one;</description></item>
/// <item><description>the appends, then the saves (version 0 inserted, others replaced if their stored version matches);</description></item>
/// <item><description>the outbox messages.</description></item>
/// </list>
/// <para>
/// Any conflict aborts the transaction and returns <c>concurrency.conflict</c>; an exception aborts
/// it as well. MongoDB itself aborts a transaction on a write conflict with another transaction;
/// the driver's <c>WithTransactionAsync</c> then runs the steps again, which re-reads the head and
/// so ends in a conflict for the slower writer. Collections and indexes must exist beforehand
/// (<see cref="LedgerIndexInitializer"/>).
/// </para>
/// </remarks>
public sealed class MongoLedger : ILedger
{
    private static readonly TransactionOptions Transaction = new(
        readConcern: ReadConcern.Snapshot,
        readPreference: ReadPreference.Primary,
        writeConcern: WriteConcern.WMajority);

    private readonly IMongoClient client;
    private readonly IMongoDatabase database;
    private readonly IOwnerContext ownerContext;
    private readonly IClock clock;
    private readonly LedgerCollections collections;

    /// <summary>Creates the ledger for the owner in <paramref name="ownerContext"/>.</summary>
    public MongoLedger(IMongoClient client, IMongoDatabase database, IOwnerContext ownerContext, IClock clock, LedgerCollections collections)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(ownerContext);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(collections);

        this.client = client;
        this.database = database;
        this.ownerContext = ownerContext;
        this.clock = clock;
        this.collections = collections;
    }

    private enum Outcome
    {
        Committed,
        AlreadyHandled,
        Conflict,
    }

    /// <inheritdoc />
    public async Task<long> GetVersionAsync(LedgerName ledger, CancellationToken cancellationToken)
    {
        EnsureRegistered(ledger);

        var heads = await Heads(ledger).FindAsync(FilterDefinition<LedgerHead>.Empty, cancellationToken: cancellationToken).ConfigureAwait(false);
        return heads.SingleOrDefault()?.Version ?? 0;
    }

    /// <inheritdoc />
    public async Task<Result<LedgerCommitted>> CommitAsync(LedgerCommit commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        EnsureRegistered(commit.Ledger);

        // Everything that is a bug is found before the transaction starts.
        var documents = commit.Appends.Cast<IDocument>().Concat(commit.Saves)
            .Select(document => (Document: document, Mapping: MappingFor(commit.Ledger, document)))
            .ToList();
        var messages = commit.Events.Select(ToOutboxMessage).ToList();
        var handled = commit.HandledEvent is null ? null : ToRecord(commit.HandledEvent);

        using var session = await client.StartSessionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var outcome = await session.WithTransactionAsync(
            (transaction, token) => WriteAsync(commit, documents, messages, handled, transaction, token),
            Transaction,
            cancellationToken).ConfigureAwait(false);

        return outcome switch
        {
            Outcome.Committed => Result.Ok(new LedgerCommitted(commit.ExpectedVersion + 1, AlreadyHandled: false)),
            Outcome.AlreadyHandled => Result.Ok(new LedgerCommitted(
                await GetVersionAsync(commit.Ledger, cancellationToken).ConfigureAwait(false),
                AlreadyHandled: true)),
            _ => Result.Fail<LedgerCommitted>(AppError.Conflict(ErrorCodes.ConcurrencyConflict)),
        };
    }

    private async Task<Outcome> WriteAsync(
        LedgerCommit commit,
        List<(IDocument Document, LedgerDocumentMapping Mapping)> documents,
        List<OutboxMessage> messages,
        ProcessedEventRecord? handled,
        IClientSessionHandle transaction,
        CancellationToken cancellationToken)
    {
        if (handled is not null
            && (await ProcessedEvents(commit.Ledger).InsertAsync(handled, transaction, cancellationToken).ConfigureAwait(false)).IsFailed)
        {
            return await AbortAsync(transaction, Outcome.AlreadyHandled, cancellationToken).ConfigureAwait(false);
        }

        if (!await AdvanceHeadAsync(commit.Ledger, commit.ExpectedVersion, transaction, cancellationToken).ConfigureAwait(false))
        {
            return await AbortAsync(transaction, Outcome.Conflict, cancellationToken).ConfigureAwait(false);
        }

        foreach (var (document, mapping) in documents)
        {
            if ((await mapping.Write(database, ownerContext, clock, document, transaction, cancellationToken).ConfigureAwait(false)).IsFailed)
            {
                return await AbortAsync(transaction, Outcome.Conflict, cancellationToken).ConfigureAwait(false);
            }
        }

        var outbox = Outbox(commit.Ledger);
        foreach (var message in messages)
        {
            if ((await outbox.InsertAsync(message, transaction, cancellationToken).ConfigureAwait(false)).IsFailed)
            {
                return await AbortAsync(transaction, Outcome.Conflict, cancellationToken).ConfigureAwait(false);
            }
        }

        return Outcome.Committed;
    }

    private static async Task<Outcome> AbortAsync(IClientSessionHandle transaction, Outcome outcome, CancellationToken cancellationToken)
    {
        await transaction.AbortTransactionAsync(cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    private async Task<bool> AdvanceHeadAsync(LedgerName ledger, long expectedVersion, IClientSessionHandle transaction, CancellationToken cancellationToken)
    {
        var heads = Heads(ledger);

        if (expectedVersion == 0)
        {
            // The unique index on ownerId makes a concurrent first commit fail here.
            var head = new LedgerHead { Id = Guid.CreateVersion7(clock.UtcNow), OwnerId = ownerContext.OwnerId };
            return (await heads.InsertAsync(head, transaction, cancellationToken).ConfigureAwait(false)).IsSuccess;
        }

        var current = (await heads.FindAsync(FilterDefinition<LedgerHead>.Empty, session: transaction, cancellationToken: cancellationToken).ConfigureAwait(false))
            .SingleOrDefault();

        return current is not null
            && current.Version == expectedVersion
            && (await heads.ReplaceAsync(current, transaction, cancellationToken).ConfigureAwait(false)).IsSuccess;
    }

    private LedgerDocumentMapping MappingFor(LedgerName ledger, IDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        EnsureCurrentOwner(document.OwnerId);

        var mapping = collections.Find(document.GetType())
            ?? throw new InvalidOperationException($"{document.GetType().Name} is not mapped to a collection.");

        return mapping.Ledger == ledger
            ? mapping
            : throw new InvalidOperationException($"{document.GetType().Name} belongs to ledger '{mapping.Ledger}', not '{ledger}'.");
    }

    private OutboxMessage ToOutboxMessage(IIntegrationEvent integrationEvent)
    {
        EnsureCurrentOwner(integrationEvent.OwnerId);
        var eventType = integrationEvent.GetType();

        return new OutboxMessage
        {
            Id = integrationEvent.EventId,
            OwnerId = integrationEvent.OwnerId,
            RecordedAt = clock.UtcNow,
            EventName = EventNameAttribute.Of(eventType),
            Payload = integrationEvent.ToBsonDocument(eventType),
        };
    }

    private ProcessedEventRecord ToRecord(ProcessedEvent handled) => new()
    {
        Id = Guid.CreateVersion7(clock.UtcNow),
        OwnerId = ownerContext.OwnerId,
        RecordedAt = clock.UtcNow,
        HandlerName = handled.HandlerName,
        EventId = handled.EventId,
    };

    private void EnsureCurrentOwner(Kernel.Ids.OwnerId owner)
    {
        if (owner != ownerContext.OwnerId)
        {
            throw new InvalidOperationException("A commit contains a document or event of another owner than the current one.");
        }
    }

    private void EnsureRegistered(LedgerName ledger)
    {
        if (ledger.Value is null || !collections.Contains(ledger))
        {
            throw new InvalidOperationException($"Ledger '{ledger}' is not registered.");
        }
    }

    private MutableCollection<LedgerHead> Heads(LedgerName ledger) =>
        new(database, LedgerCollections.HeadsCollection(ledger), ownerContext, clock);

    private AppendOnlyCollection<ProcessedEventRecord> ProcessedEvents(LedgerName ledger) =>
        new(database, LedgerCollections.ProcessedEventsCollection(ledger), ownerContext);

    private AppendOnlyCollection<OutboxMessage> Outbox(LedgerName ledger) =>
        new(database, LedgerCollections.OutboxCollection(ledger), ownerContext);
}
