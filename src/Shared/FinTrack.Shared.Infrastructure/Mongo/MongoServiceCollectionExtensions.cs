using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.Mongo;

/// <summary>Registers the MongoDB connection.</summary>
public static class MongoServiceCollectionExtensions
{
    private const string ApplicationName = "FinTrack";

    /// <summary>
    /// Registers the storage format (<see cref="MongoSerialization.Register"/>), one
    /// <see cref="IMongoClient"/> for the whole process, the application's <see cref="IMongoDatabase"/>
    /// and the <see cref="MongoIndexRunner"/>.
    /// </summary>
    /// <remarks>
    /// The client is a singleton because it owns the connection pool; creating one per request
    /// would open new connections every time. Invalid options fail when the client is first resolved.
    /// </remarks>
    public static IServiceCollection AddFinTrackMongo(this IServiceCollection services, Action<MongoOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        MongoSerialization.Register();

        services.AddOptions<MongoOptions>().Configure(configure).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<MongoOptions>, MongoOptionsValidator>());

        services.TryAddSingleton<IMongoClient>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<MongoOptions>>().Value;
            var settings = MongoClientSettings.FromConnectionString(options.ConnectionString);
            settings.ApplicationName = ApplicationName;
            return new MongoClient(settings);
        });

        services.TryAddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<MongoOptions>>().Value;
            return provider.GetRequiredService<IMongoClient>().GetDatabase(options.DatabaseName);
        });

        services.TryAddSingleton<MongoIndexRunner>();

        return services;
    }

    /// <summary>Registers one context's index initializer for <see cref="MongoIndexRunner"/>.</summary>
    public static IServiceCollection AddMongoIndexInitializer<TInitializer>(this IServiceCollection services)
        where TInitializer : class, IMongoIndexInitializer
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMongoIndexInitializer, TInitializer>());
        return services;
    }
}
