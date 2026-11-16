using FinTrack.Shared.Infrastructure.Mongo;
using FinTrack.Shared.Infrastructure.Outbox;
using FinTrack.Shared.Infrastructure.Owners;
using FinTrack.Shared.Kernel.Persistence;
using FinTrack.Shared.Kernel.Ledger;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FinTrack.Shared.Infrastructure.Ledger;

/// <summary>Registers <see cref="ILedger"/> on MongoDB.</summary>
public static class LedgerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the write path: the scoped <see cref="ILedger"/> (a <see cref="DispatchingLedger"/>
    /// around <see cref="MongoLedger"/>, so committed events are delivered right away), the
    /// <see cref="OutboxDispatcher"/> and its event registry, the <see cref="ScopedOwnerContext"/> as
    /// <see cref="IOwnerContext"/>, and the <see cref="LedgerIndexInitializer"/>. Needs
    /// <see cref="MongoServiceCollectionExtensions.AddFinTrackMongo"/>, an <c>IClock</c> and logging.
    /// </summary>
    public static IServiceCollection AddMongoLedger(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(provider => new LedgerCollections(provider.GetServices<LedgerDocuments>()));
        services.TryAddScoped<MongoLedger>();
        services.TryAddScoped<ILedger>(provider => new DispatchingLedger(
            provider.GetRequiredService<MongoLedger>(),
            provider.GetRequiredService<OutboxDispatcher>()));

        services.TryAddScoped<ScopedOwnerContext>();
        services.TryAddScoped<IOwnerContext>(provider => provider.GetRequiredService<ScopedOwnerContext>());

        services.TryAddSingleton(provider => new IntegrationEventRegistry(provider.GetServices<IntegrationEventRegistration>()));
        services.TryAddSingleton<OutboxDispatcher>();
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
