using FinTrack.Api.IntegrationTests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

[assembly: AssemblyFixture(typeof(ApiFixture))]

namespace FinTrack.Api.IntegrationTests;

/// <summary>
/// The Api's own composition (<see cref="ApiComposition.AddFinTrackApi"/>) against a real MongoDB,
/// with a database of its own that is dropped at the end. The server comes from
/// <c>FINTRACK_TEST_MONGO</c> (default: the local single-node replica set).
/// </summary>
public sealed class ApiFixture : IAsyncDisposable
{
    /// <summary>An invented owner for local mode.</summary>
    public const string LocalOwnerId = "0c0ffee0-0000-4000-8000-000000000042";

    private const string DefaultConnectionString = "mongodb://localhost:27017/?replicaSet=rs0";

    public ApiFixture()
    {
        ConnectionString = Environment.GetEnvironmentVariable("FINTRACK_TEST_MONGO") is { Length: > 0 } configured
            ? configured
            : DefaultConnectionString;
        DatabaseName = $"fintrack_api_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..44];
        Services = BuildServices(ConnectionString, DatabaseName);
    }

    public string ConnectionString { get; }

    public string DatabaseName { get; }

    public ServiceProvider Services { get; }

    /// <summary>Builds the Api's container with the given settings, as <c>Program.cs</c> does.</summary>
    public static ServiceProvider BuildServices(string connectionString, string databaseName, string localOwnerId = LocalOwnerId, IReadOnlyDictionary<string, string?>? more = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mongo:ConnectionString"] = connectionString,
                ["Mongo:DatabaseName"] = databaseName,
                ["FinTrack:LocalOwnerId"] = localOwnerId,
            })
            .AddInMemoryCollection(more ?? new Dictionary<string, string?>())
            .Build();

        return new ServiceCollection()
            .AddLogging()
            .AddFinTrackApi(configuration)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public async ValueTask DisposeAsync()
    {
        await Services.GetRequiredService<IMongoClient>().DropDatabaseAsync(DatabaseName);
        await Services.DisposeAsync();
    }
}
