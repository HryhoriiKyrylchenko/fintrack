using FinTrack.Budget.Application;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.Settings;
using FinTrack.Shared.Infrastructure.Mongo;
using FinTrack.Shared.Kernel.Persistence;
using FinTrack.Shared.Kernel.Time;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FinTrack.Budget.Infrastructure;

/// <summary>Reads Budget's documents through the owner-scoped collections, so every read is limited to the current owner.</summary>
internal sealed class MongoBudgetStore(IMongoDatabase database, IOwnerContext owner, IClock clock) : IBudgetStore
{
    // The replay order (ARCHITECTURE 4.2) as stored: "occurredOn" is yyyy-MM-dd text, which sorts by date;
    // "_id" is a standard UUID, compared by its bytes like ReplayOrder.CompareIds. The index "replay_order" serves it.
    private static readonly SortDefinition<JournalEntry> ReplaySort =
        new BsonDocumentSortDefinition<JournalEntry>(new BsonDocument { { "occurredOn", 1 }, { "recordedAt", 1 }, { "_id", 1 } });

    public async Task<OwnerSettings?> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = new MutableCollection<OwnerSettings>(database, BudgetCollections.Settings, owner, clock);
        var found = await settings.FindAsync(FilterDefinition<OwnerSettings>.Empty, cancellationToken: cancellationToken).ConfigureAwait(false);

        // The unique index allows at most one per owner.
        return found.SingleOrDefault();
    }

    public async Task<IReadOnlyList<Bucket>> GetBucketsAsync(CancellationToken cancellationToken)
    {
        var buckets = new MutableCollection<Bucket>(database, BudgetCollections.Buckets, owner, clock);
        return await buckets.FindAsync(FilterDefinition<Bucket>.Empty, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<JournalEntry>> GetJournalAsync(CancellationToken cancellationToken)
    {
        var journal = new AppendOnlyCollection<JournalEntry>(database, BudgetCollections.Journal, owner);
        return await journal.FindAsync(FilterDefinition<JournalEntry>.Empty, ReplaySort, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
