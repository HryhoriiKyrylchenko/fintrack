using FinTrack.Shared.Infrastructure.Mongo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.IntegrationTests.Mongo;

/// <summary>Registration of the connection. Creating a client does not contact the server.</summary>
public sealed class MongoRegistrationTests
{
    private const string ValidConnectionString = "mongodb://localhost:27017/?replicaSet=rs0";

    [Fact]
    public void Register_CanBeCalledRepeatedly() =>
        Should.NotThrow(() =>
        {
            MongoSerialization.Register();
            MongoSerialization.Register();
        });

    [Fact]
    public void AddFinTrackMongo_RegistersOneClientAndTheDatabase()
    {
        using var provider = BuildProvider(ValidConnectionString, "fintrack");

        var client = provider.GetRequiredService<IMongoClient>();

        provider.GetRequiredService<IMongoClient>().ShouldBeSameAs(client);
        client.Settings.ApplicationName.ShouldBe("FinTrack");
        provider.GetRequiredService<IMongoDatabase>().DatabaseNamespace.DatabaseName.ShouldBe("fintrack");
    }

    [Theory]
    [InlineData("", "fintrack")]
    [InlineData("not a connection string", "fintrack")]
    [InlineData(ValidConnectionString, "")]
    [InlineData(ValidConnectionString, "fin.track")]
    [InlineData(ValidConnectionString, "fin track")]
    [InlineData(ValidConnectionString, "fin/track")]
    [InlineData(ValidConnectionString, "fintrack$")]
    public void InvalidOptions_FailWhenTheClientIsResolved(string connectionString, string databaseName)
    {
        using var provider = BuildProvider(connectionString, databaseName);

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IMongoClient>());
    }

    [Fact]
    public void DatabaseNameLongerThan63Characters_IsRejected()
    {
        using var provider = BuildProvider(ValidConnectionString, new string('a', 64));

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IMongoClient>());
    }

    [Fact]
    public void ValidationFailure_NeverShowsTheConnectionString()
    {
        using var provider = BuildProvider("mongodb://fintrack:invented-password@localhost:27017/?replicaSet=rs0", "fin.track");

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IMongoClient>());

        exception.Message.ShouldNotContain("invented-password");
        exception.Failures.ShouldAllBe(failure => !failure.Contains("invented-password"));
    }

    private static ServiceProvider BuildProvider(string connectionString, string databaseName) =>
        new ServiceCollection()
            .AddFinTrackMongo(options =>
            {
                options.ConnectionString = connectionString;
                options.DatabaseName = databaseName;
            })
            .BuildServiceProvider();
}
