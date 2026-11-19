using FinTrack.Budget.Infrastructure.IntegrationTests;
using FinTrack.Shared.Infrastructure.Ledger;
using FinTrack.Shared.Infrastructure.Mongo;
using FinTrack.Shared.Infrastructure.Owners;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Time;
using FinTrack.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

[assembly: AssemblyFixture(typeof(BudgetFixture))]

namespace FinTrack.Budget.Infrastructure.IntegrationTests;

/// <summary>
/// Budget registered as the Api registers it (shared MongoDB setup, ledger, <see cref="BudgetServiceCollectionExtensions.AddBudget"/>)
/// against a real MongoDB, in a database of its own that is dropped at the end. The server comes from
/// <c>FINTRACK_TEST_MONGO</c> (default: the local single-node replica set). The clock is a
/// <see cref="FakeClock"/>, so stored instants are known; exchange rates come from <see cref="FixedFxRates"/>,
/// never from the network.
/// </summary>
public sealed class BudgetFixture : IAsyncLifetime
{
    private const string DefaultConnectionString = "mongodb://localhost:27017/?replicaSet=rs0";

    public BudgetFixture()
    {
        var connectionString = Environment.GetEnvironmentVariable("FINTRACK_TEST_MONGO") is { Length: > 0 } configured
            ? configured
            : DefaultConnectionString;
        DatabaseName = $"fintrack_budget_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..44];

        Services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<IClock>(new FakeClock())
            .AddSingleton<IFxRateProvider>(FixedFxRates.Instance)
            .AddFinTrackMongo(options =>
            {
                options.ConnectionString = connectionString;
                options.DatabaseName = DatabaseName;
            })
            .AddMongoLedger()
            .AddBudget()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public string DatabaseName { get; }

    public ServiceProvider Services { get; }

    public IMongoDatabase Database => Services.GetRequiredService<IMongoDatabase>();

    /// <summary>The stored documents of a collection, exactly as MongoDB holds them.</summary>
    public IMongoCollection<BsonDocument> Raw(string collectionName) => Database.GetCollection<BsonDocument>(collectionName);

    /// <summary>A scope acting for <paramref name="owner"/>, as the owner middleware sets it per request.</summary>
    public AsyncServiceScope ScopeFor(OwnerId owner)
    {
        var scope = Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedOwnerContext>().Set(owner);
        return scope;
    }

    /// <summary>A new owner that no other test uses, so tests do not see each other's documents.</summary>
    public static OwnerId NewOwner() => OwnerId.From(Guid.NewGuid());

    public async ValueTask InitializeAsync() =>
        await Services.GetRequiredService<MongoIndexRunner>().EnsureCreatedAsync(CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await Services.GetRequiredService<IMongoClient>().DropDatabaseAsync(DatabaseName);
        await Services.DisposeAsync();
    }
}
