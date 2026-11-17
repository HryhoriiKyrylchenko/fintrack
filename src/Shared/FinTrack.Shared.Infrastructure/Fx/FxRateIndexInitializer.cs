using FinTrack.Shared.Infrastructure.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.Fx;

/// <summary>
/// <c>ref_fx_rates</c>: unique (<c>base</c>, <c>date</c>), so a rate date is stored once and
/// <see cref="ReferenceCollection{TDocument}.UpsertAsync"/> can rely on it.
/// </summary>
internal sealed class FxRateIndexInitializer : IMongoIndexInitializer
{
    public Task CreateIndexesAsync(IMongoDatabase database, CancellationToken cancellationToken) =>
        CreateIndexAsync(database, FxRateSet.CollectionName, cancellationToken);

    /// <summary>Creates the index on <paramref name="collectionName"/>; tests use their own collections.</summary>
    internal static async Task CreateIndexAsync(IMongoDatabase database, string collectionName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        await database.GetCollection<BsonDocument>(collectionName).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                new BsonDocument { { "base", 1 }, { "date", 1 } },
                new CreateIndexOptions { Unique = true, Name = "base_date_unique" }),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
