using FinTrack.Shared.Infrastructure.IntegrationTests;
using FinTrack.Shared.Infrastructure.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

[assembly: AssemblyFixture(typeof(MongoDatabaseFixture))]

namespace FinTrack.Shared.Infrastructure.IntegrationTests;

/// <summary>
/// One real MongoDB database for the whole test run, with a unique name, dropped at the end.
/// The server comes from <c>FINTRACK_TEST_MONGO</c> (default: the local single-node replica set);
/// it must be a replica set, because later tests use transactions.
/// </summary>
public sealed class MongoDatabaseFixture : IAsyncLifetime
{
    public const string ConnectionVariable = "FINTRACK_TEST_MONGO";
    private const string DefaultConnectionString = "mongodb://localhost:27017/?replicaSet=rs0";

    private readonly string safeConnectionString;

    public MongoDatabaseFixture()
    {
        MongoSerialization.Register();

        var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable) is { Length: > 0 } configured
            ? configured
            : DefaultConnectionString;
        safeConnectionString = new MongoUrlBuilder(connectionString) { Password = null }.ToString();

        var settings = MongoClientSettings.FromConnectionString(connectionString);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        Client = new MongoClient(settings);

        DatabaseName = $"fintrack_it_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..44];
        Database = Client.GetDatabase(DatabaseName);
    }

    public IMongoClient Client { get; }

    public string DatabaseName { get; }

    public IMongoDatabase Database { get; }

    /// <summary>A new, empty collection with a unique name, typed and raw (to inspect the stored format).</summary>
    public (IMongoCollection<T> Typed, IMongoCollection<BsonDocument> Raw) NewCollection<T>()
    {
        var name = $"it_{Guid.NewGuid():N}";
        return (Database.GetCollection<T>(name), Database.GetCollection<BsonDocument>(name));
    }

    public async ValueTask InitializeAsync()
    {
        BsonDocument hello;
        try
        {
            hello = await Database.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1));
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(
                $"MongoDB is not reachable at {safeConnectionString}. Start it, or set {ConnectionVariable}. See docs/LOCAL-SETUP.md.",
                exception);
        }

        if (!hello.Contains("setName"))
        {
            throw new InvalidOperationException(
                $"MongoDB at {safeConnectionString} is not a replica set; transactions need one. See docs/LOCAL-SETUP.md.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DropDatabaseAsync(DatabaseName);
        Client.Dispose();
    }
}
