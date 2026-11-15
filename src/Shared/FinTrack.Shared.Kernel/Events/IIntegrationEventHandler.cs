using FinTrack.Shared.Kernel.Ledger;
using FluentResults;

namespace FinTrack.Shared.Kernel.Events;

/// <summary>
/// Handles one kind of integration event in the receiving context. Delivery is at least once,
/// so a handler must be idempotent: it commits its writes with <see cref="LedgerCommit.HandledEvent"/>
/// set, and a repeated delivery is then recognised and writes nothing (invariant 13).
/// </summary>
/// <typeparam name="TEvent">The event type.</typeparam>
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IIntegrationEvent
{
    /// <summary>Handles the event under the event's owner.</summary>
    Task<Result> HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken);
}
