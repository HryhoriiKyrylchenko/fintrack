using FinTrack.Shared.Infrastructure.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.Ledger;

/// <summary>
/// Creates every ledger's collections and the indexes the ledger and the outbox rely on, before
/// any transaction uses them: a unique <c>ownerId</c> on the heads (one head per owner), a unique
/// (<c>ownerId</c>, <c>handlerName</c>, <c>eventId</c>) on processed events (each delivery handled once)
/// and (<c>dispatchedAt</c>, <c>_id</c>) on the outbox (the sweep's query for pending messages).
/// </summary>
/// <remarks>Element names are the stored ones (camelCase, ARCHITECTURE 2.2); tests check them.</remarks>
public sealed class LedgerIndexInitializer(LedgerCollections collections) : IMongoIndexInitializer
{
    private const int NamespaceExists = 48;

    /// <inheritdoc />
    public async Task CreateIndexesAsync(IMongoDatabase database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        using var cursor = await database.ListCollectionNamesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var existing = (await cursor.ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);

        var names = collections.Ledgers
            .SelectMany(ledger => new[]
            {
                LedgerCollections.HeadsCollection(ledger),
                LedgerCollections.OutboxCollection(ledger),
                LedgerCollections.ProcessedEventsCollection(ledger),
            })
            .Concat(collections.Mappings.Select(mapping => mapping.CollectionName));

        foreach (var name in names.Where(name => !existing.Contains(name)))
        {
            await CreateCollectionAsync(database, name, cancellationToken).ConfigureAwait(false);
        }

        foreach (var ledger in collections.Ledgers)
        {
            await CreateUniqueIndexAsync(
                database,
                LedgerCollections.HeadsCollection(ledger),
                new BsonDocument("ownerId", 1),
                "ownerId_unique",
                cancellationToken).ConfigureAwait(false);

            await CreateUniqueIndexAsync(
                database,
                LedgerCollections.ProcessedEventsCollection(ledger),
                new BsonDocument { { "ownerId", 1 }, { "handlerName", 1 }, { "eventId", 1 } },
                "ownerId_handlerName_eventId_unique",
                cancellationToken).ConfigureAwait(false);

            await database.GetCollection<BsonDocument>(LedgerCollections.OutboxCollection(ledger)).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    new BsonDocument { { "dispatchedAt", 1 }, { "_id", 1 } },
                    new CreateIndexOptions { Name = "pending" }),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CreateCollectionAsync(IMongoDatabase database, string name, CancellationToken cancellationToken)
    {
        try
        {
            await database.CreateCollectionAsync(name, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (MongoCommandException exception) when (exception.Code == NamespaceExists)
        {
            // Another process created it in the meantime.
        }
    }

    private static Task CreateUniqueIndexAsync(IMongoDatabase database, string collectionName, BsonDocument keys, string indexName, CancellationToken cancellationToken) =>
        database.GetCollection<BsonDocument>(collectionName).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(keys, new CreateIndexOptions { Unique = true, Name = indexName }),
            cancellationToken: cancellationToken);
}
