using FinTrack.Shared.Infrastructure.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FinTrack.Budget.Infrastructure;

/// <summary>
/// Budget's indexes, each a backstop: the ledger version already makes an owner's writes conflict.
/// <list type="bullet">
/// <item><description><c>budget_settings</c>: unique <c>ownerId</c> (one settings document per owner);</description></item>
/// <item><description><c>budget_buckets</c>: unique (<c>ownerId</c>, <c>systemRole</c>, <c>currency</c>) for system buckets only
/// (a partial index), so there is at most one <c>Unallocated</c> per currency (plan 1a D5);</description></item>
/// <item><description><c>budget_journal</c>: (<c>ownerId</c>, <c>occurredOn</c>, <c>recordedAt</c>, <c>_id</c>), the replay order
/// (ARCHITECTURE 4.2), so the owner's journal is read sorted without a sort in memory on the server.</description></item>
/// </list>
/// Bucket names have no index: they are unique without regard to case and only among active buckets,
/// which the domain checks against the owner's buckets read under the ledger version.
/// </summary>
/// <remarks>Element names are the stored ones; the integration tests check them.</remarks>
internal sealed class BudgetIndexInitializer : IMongoIndexInitializer
{
    public async Task CreateIndexesAsync(IMongoDatabase database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        await database.GetCollection<BsonDocument>(BudgetCollections.Settings).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                new BsonDocument("ownerId", 1),
                new CreateIndexOptions { Unique = true, Name = "ownerId_unique" }),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await database.GetCollection<BsonDocument>(BudgetCollections.Buckets).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                new BsonDocument { { "ownerId", 1 }, { "systemRole", 1 }, { "currency", 1 } },
                new CreateIndexOptions<BsonDocument>
                {
                    Unique = true,
                    Name = "unallocated_per_currency_unique",
                    PartialFilterExpression = new BsonDocument("systemRole", "Unallocated"),
                }),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await database.GetCollection<BsonDocument>(BudgetCollections.Journal).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                new BsonDocument { { "ownerId", 1 }, { "occurredOn", 1 }, { "recordedAt", 1 }, { "_id", 1 } },
                new CreateIndexOptions { Name = "replay_order" }),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
