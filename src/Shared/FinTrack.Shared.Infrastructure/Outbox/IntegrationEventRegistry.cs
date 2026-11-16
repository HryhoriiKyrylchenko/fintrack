using FinTrack.Shared.Kernel.Events;
using FluentResults;
using Microsoft.Extensions.DependencyInjection;

namespace FinTrack.Shared.Infrastructure.Outbox;

/// <summary>
/// Maps stored event names back to event types. Stored messages carry the stable
/// <c>[EventName]</c>, never a CLR type name, so this registry is the only link between them.
/// </summary>
public sealed class IntegrationEventRegistry
{
    private readonly Dictionary<string, IntegrationEventRegistration> byName = new(StringComparer.Ordinal);

    /// <summary>Builds the registry; one type per name (a type registered twice counts once).</summary>
    public IntegrationEventRegistry(IEnumerable<IntegrationEventRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        foreach (var registration in registrations)
        {
            if (byName.TryGetValue(registration.EventName, out var existing) && existing.EventType != registration.EventType)
            {
                throw new InvalidOperationException(
                    $"Event name '{registration.EventName}' is used by {existing.EventType.Name} and {registration.EventType.Name}.");
            }

            byName[registration.EventName] = registration;
        }
    }

    /// <summary>The registration for <paramref name="eventName"/>, or <c>null</c> when no type has it.</summary>
    public IntegrationEventRegistration? Find(string eventName) => byName.GetValueOrDefault(eventName);
}

/// <summary>One event type, its stable name, and how to run all its handlers.</summary>
public sealed class IntegrationEventRegistration
{
    private IntegrationEventRegistration(Type eventType, string eventName, Func<IServiceProvider, IIntegrationEvent, CancellationToken, Task<Result>> handle)
    {
        EventType = eventType;
        EventName = eventName;
        Handle = handle;
    }

    /// <summary>The event type.</summary>
    public Type EventType { get; }

    /// <summary>Its stable name.</summary>
    public string EventName { get; }

    /// <summary>Runs every registered handler in order and stops at the first failure.</summary>
    internal Func<IServiceProvider, IIntegrationEvent, CancellationToken, Task<Result>> Handle { get; }

    /// <summary>Creates the registration of <typeparamref name="TEvent"/>; throws when it has no <c>[EventName]</c>.</summary>
    public static IntegrationEventRegistration For<TEvent>()
        where TEvent : IIntegrationEvent =>
        new(typeof(TEvent), EventNameAttribute.Of(typeof(TEvent)), HandleAllAsync<TEvent>);

    private static async Task<Result> HandleAllAsync<TEvent>(IServiceProvider services, IIntegrationEvent integrationEvent, CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent
    {
        foreach (var handler in services.GetServices<IIntegrationEventHandler<TEvent>>())
        {
            var result = await handler.HandleAsync((TEvent)integrationEvent, cancellationToken).ConfigureAwait(false);
            if (result.IsFailed)
            {
                return result;
            }
        }

        return Result.Ok();
    }
}
