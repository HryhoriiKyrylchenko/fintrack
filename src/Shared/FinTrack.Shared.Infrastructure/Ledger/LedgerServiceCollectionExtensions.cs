using FinTrack.Shared.Infrastructure.Mongo;
using FinTrack.Shared.Kernel.Ledger;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FinTrack.Shared.Infrastructure.Ledger;

/// <summary>Registers <see cref="ILedger"/> on MongoDB.</summary>
public static class LedgerServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="MongoLedger"/> as the scoped <see cref="ILedger"/> (it works for the
    /// request's owner) and the <see cref="LedgerIndexInitializer"/>. Needs
    /// <see cref="MongoServiceCollectionExtensions.AddFinTrackMongo"/>, an <c>IOwnerContext</c> and an <c>IClock</c>.
    /// </summary>
    public static IServiceCollection AddMongoLedger(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(provider => new LedgerCollections(provider.GetServices<LedgerDocuments>()));
        services.TryAddScoped<ILedger, MongoLedger>();
        services.AddMongoIndexInitializer<LedgerIndexInitializer>();

        return services;
    }

    /// <summary>Registers one context's ledger and the collections of its document types.</summary>
    public static IServiceCollection AddLedgerDocuments(this IServiceCollection services, LedgerDocuments documents)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(documents);

        services.AddSingleton(documents);
        return services;
    }
}
