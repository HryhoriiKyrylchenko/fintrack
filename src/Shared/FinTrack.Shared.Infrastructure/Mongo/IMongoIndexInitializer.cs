using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.Mongo;

/// <summary>
/// Creates the indexes of one context's collections. Each context implements one and registers it
/// with <see cref="MongoServiceCollectionExtensions.AddMongoIndexInitializer{TInitializer}"/>;
/// <see cref="MongoIndexRunner"/> runs them once per process start.
/// </summary>
/// <remarks>
/// Creating an index that already exists with the same definition does nothing, so an initializer
/// may run on every start. Indexes (and so their collections) exist before the first transaction
/// uses them. Unlike EF Core migrations there is no schema to create, only indexes.
/// </remarks>
public interface IMongoIndexInitializer
{
    /// <summary>Creates the indexes; must be safe to run again.</summary>
    Task CreateIndexesAsync(IMongoDatabase database, CancellationToken cancellationToken);
}
