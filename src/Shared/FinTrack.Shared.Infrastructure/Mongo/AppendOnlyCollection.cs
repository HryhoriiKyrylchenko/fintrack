using FinTrack.Shared.Kernel.Persistence;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.Mongo;

/// <summary>
/// A collection of documents that are inserted once and never changed or removed: journal
/// entries, outbox messages, processed-event records (CLAUDE.md rule 5). It has no update or
/// delete method at all, so the rule cannot be broken through it.
/// </summary>
/// <typeparam name="TDocument">The stored type.</typeparam>
public sealed class AppendOnlyCollection<TDocument> : OwnerScopedCollection<TDocument>
    where TDocument : class, IAppendOnlyDocument
{
    /// <summary>Opens the collection <paramref name="collectionName"/> for the owner in <paramref name="ownerContext"/>.</summary>
    public AppendOnlyCollection(IMongoDatabase database, string collectionName, IOwnerContext ownerContext)
        : base(database, collectionName, ownerContext)
    {
    }
}
