using FinTrack.Shared.Kernel.Persistence;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.Mongo;

/// <summary>What all reference-data collections share.</summary>
public static class ReferenceCollection
{
    /// <summary>The prefix of every reference-data collection, and of no other collection.</summary>
    public const string NamePrefix = "ref_";
}

/// <summary>
/// A collection of reference data shared by all owners, for example FX rates (ARCHITECTURE 2.2,
/// plan 1a D8). It is the second documented exception to owner scoping (2.3): its reads and writes
/// have no owner filter, so it accepts only collections named <c>ref_…</c> and only types that are
/// not owner documents. Owner data is never stored here, and <see cref="OwnerScopedCollection{TDocument}"/>
/// refuses <c>ref_</c> collections in turn.
/// </summary>
/// <remarks>
/// Reference documents are written by the system, not by commands, so they are not part of a
/// ledger commit: a write here is a single-document upsert outside any transaction.
/// </remarks>
/// <typeparam name="TDocument">The stored type.</typeparam>
public sealed class ReferenceCollection<TDocument>
    where TDocument : class, IReferenceDocument
{
    private const string IdElement = "_id";

    private readonly IMongoCollection<TDocument> typed;

    /// <summary>Opens <paramref name="collectionName"/>, which must start with <see cref="ReferenceCollection.NamePrefix"/>.</summary>
    /// <exception cref="ArgumentException">The name does not start with <see cref="ReferenceCollection.NamePrefix"/>.</exception>
    /// <exception cref="InvalidOperationException"><typeparamref name="TDocument"/> is an owner document (a bug).</exception>
    public ReferenceCollection(IMongoDatabase database, string collectionName)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

        if (!collectionName.StartsWith(ReferenceCollection.NamePrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"A reference-data collection name starts with '{ReferenceCollection.NamePrefix}'.", nameof(collectionName));
        }

        if (typeof(IDocument).IsAssignableFrom(typeof(TDocument)))
        {
            throw new InvalidOperationException($"{typeof(TDocument).Name} is an owner document; owner data is never reference data.");
        }

        // The format must be known before the driver looks up the serializer of TDocument.
        MongoSerialization.Register();
        TDocument.RegisterMapping();
        typed = database.GetCollection<TDocument>(collectionName);
    }

    /// <summary>The first document that matches <paramref name="filter"/> in the order of <paramref name="sort"/>, or <c>null</c>.</summary>
    public async Task<TDocument?> FindFirstAsync(FilterDefinition<TDocument> filter, SortDefinition<TDocument> sort, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(sort);

        return await typed.Find(filter).Sort(sort).Limit(1).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores <paramref name="document"/> as the one document that matches <paramref name="key"/>: a
    /// new one with the document's <c>Id</c>, or the existing one with every field replaced except its
    /// <c>Id</c>. The key must have a unique index; two writers racing to insert the same key end with
    /// one document.
    /// </summary>
    public async Task UpsertAsync(FilterDefinition<TDocument> key, TDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(document);

        var stored = document.ToBsonDocument(BsonSerializer.LookupSerializer<TDocument>());
        var fields = new BsonDocument(stored.Where(element => element.Name != IdElement));
        var update = new BsonDocumentUpdateDefinition<TDocument>(new BsonDocument
        {
            { "$set", fields },
            { "$setOnInsert", new BsonDocument(IdElement, stored[IdElement]) },
        });
        var options = new UpdateOptions { IsUpsert = true };

        try
        {
            await typed.UpdateOneAsync(key, update, options, cancellationToken).ConfigureAwait(false);
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Another writer inserted the same key in between; now the update matches it.
            await typed.UpdateOneAsync(key, update, options, cancellationToken).ConfigureAwait(false);
        }
    }
}
