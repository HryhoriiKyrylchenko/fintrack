using FinTrack.Shared.Infrastructure.Mongo;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.IntegrationTests.Mongo.Collections;

/// <summary>Index initializers run once per process, before collections are used.</summary>
public sealed class MongoIndexRunnerTests(MongoDatabaseFixture fixture)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EnsureCreated_RunsEveryInitializerOnce_EvenWhenCalledConcurrently()
    {
        var first = new CountingInitializer($"it_{Guid.NewGuid():N}");
        var second = new CountingInitializer($"it_{Guid.NewGuid():N}");
        var runner = new MongoIndexRunner(fixture.Database, [first, second]);

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => runner.EnsureCreatedAsync(Cancellation)));
        await runner.EnsureCreatedAsync(Cancellation);

        first.Runs.ShouldBe(1);
        second.Runs.ShouldBe(1);
        (await IndexNamesAsync(first.CollectionName)).ShouldContain(CountingInitializer.IndexName);
        (await IndexNamesAsync(second.CollectionName)).ShouldContain(CountingInitializer.IndexName);
    }

    [Fact]
    public async Task EnsureCreated_AfterAFailure_TriesAgain()
    {
        var flaky = new FailingOnceInitializer();
        var runner = new MongoIndexRunner(fixture.Database, [flaky]);

        await Should.ThrowAsync<InvalidOperationException>(() => runner.EnsureCreatedAsync(Cancellation));
        await runner.EnsureCreatedAsync(Cancellation);

        flaky.Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task AddMongoIndexInitializer_RegistersTheInitializerForTheRunner()
    {
        using var provider = new ServiceCollection()
            .AddFinTrackMongo(options =>
            {
                options.ConnectionString = "mongodb://localhost:27017/?replicaSet=rs0";
                options.DatabaseName = "unused";
            })
            .AddSingleton(fixture.Database)
            .AddMongoIndexInitializer<RegisteredInitializer>()
            .BuildServiceProvider();

        await provider.GetRequiredService<MongoIndexRunner>().EnsureCreatedAsync(Cancellation);

        (await IndexNamesAsync(RegisteredInitializer.CollectionName)).ShouldContain(CountingInitializer.IndexName);
    }

    private async Task<List<string>> IndexNamesAsync(string collectionName)
    {
        using var cursor = await fixture.Database.GetCollection<BsonDocument>(collectionName).Indexes.ListAsync(Cancellation);
        var indexes = await cursor.ToListAsync(Cancellation);
        return [.. indexes.Select(index => index["name"].AsString)];
    }

    private sealed class CountingInitializer(string collectionName) : IMongoIndexInitializer
    {
        public const string IndexName = "ownerId_1__id_1";

        private int runs;

        public string CollectionName => collectionName;

        public int Runs => runs;

        public async Task CreateIndexesAsync(IMongoDatabase database, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref runs);
            await database.GetCollection<BsonDocument>(collectionName).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("ownerId").Ascending("_id")),
                cancellationToken: cancellationToken);
        }
    }

    // The database is unique per test run, so a fixed collection name is safe.
    private sealed class RegisteredInitializer : IMongoIndexInitializer
    {
        public const string CollectionName = "it_registered_initializer";

        public Task CreateIndexesAsync(IMongoDatabase database, CancellationToken cancellationToken) =>
            new CountingInitializer(CollectionName).CreateIndexesAsync(database, cancellationToken);
    }

    private sealed class FailingOnceInitializer : IMongoIndexInitializer
    {
        public int Attempts { get; private set; }

        public Task CreateIndexesAsync(IMongoDatabase database, CancellationToken cancellationToken)
        {
            Attempts++;
            return Attempts == 1 ? throw new InvalidOperationException("Simulated failure.") : Task.CompletedTask;
        }
    }
}
