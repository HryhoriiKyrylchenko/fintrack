using FinTrack.Shared.Kernel.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FinTrack.Shared.Infrastructure.Outbox;

/// <summary>Registers integration events and their handlers.</summary>
public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Makes <typeparamref name="TEvent"/> known under its <c>[EventName]</c>, so stored messages can be
    /// read back. Throws when the type has no name.
    /// </summary>
    public static IServiceCollection AddIntegrationEvent<TEvent>(this IServiceCollection services)
        where TEvent : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(IntegrationEventRegistration.For<TEvent>());
        return services;
    }

    /// <summary>Registers <typeparamref name="THandler"/> (scoped) for <typeparamref name="TEvent"/>, and the event itself.</summary>
    public static IServiceCollection AddIntegrationEventHandler<TEvent, THandler>(this IServiceCollection services)
        where TEvent : IIntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        services.AddIntegrationEvent<TEvent>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IIntegrationEventHandler<TEvent>, THandler>());
        return services;
    }
}
