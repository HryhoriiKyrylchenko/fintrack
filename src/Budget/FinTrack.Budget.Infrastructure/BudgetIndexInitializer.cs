using FinTrack.Shared.Infrastructure.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FinTrack.Budget.Infrastructure;

/// <summary>
/// Budget's indexes: a unique <c>ownerId</c> on <c>budget_settings</c>, the backstop for "one
/// settings document per owner" (two concurrent first saves already conflict on the ledger version).
/// </summary>
/// <remarks>Element names are the stored ones; the integration tests check them.</remarks>
internal sealed class BudgetIndexInitializer : IMongoIndexInitializer
{
    public Task CreateIndexesAsync(IMongoDatabase database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        return database.GetCollection<BsonDocument>(BudgetCollections.Settings).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                new BsonDocument("ownerId", 1),
                new CreateIndexOptions { Unique = true, Name = "ownerId_unique" }),
            cancellationToken: cancellationToken);
    }
}
