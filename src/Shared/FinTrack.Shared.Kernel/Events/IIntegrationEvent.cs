using FinTrack.Shared.Kernel.Ids;

namespace FinTrack.Shared.Kernel.Events;

/// <summary>
/// A fact one context publishes for others, delivered through the transactional outbox
/// (CLAUDE.md rule 8). Named in the past tense (<c>DepositAdded</c>) and marked with
/// <see cref="EventNameAttribute"/>. Event types of a context live in its Contracts project.
/// </summary>
public interface IIntegrationEvent
{
    /// <summary>Unique identifier (version 7 UUID); handlers use it to recognise a repeated delivery.</summary>
    Guid EventId { get; }

    /// <summary>The owner; the event is handled under this owner.</summary>
    OwnerId OwnerId { get; }

    /// <summary>When it happened (from <c>IClock</c>).</summary>
    DateTimeOffset OccurredAt { get; }
}
