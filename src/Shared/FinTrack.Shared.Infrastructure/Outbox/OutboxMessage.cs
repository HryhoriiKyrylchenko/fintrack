using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Persistence;
using MongoDB.Bson;

namespace FinTrack.Shared.Infrastructure.Outbox;

/// <summary>
/// An integration event waiting for delivery, stored in the same transaction as the data that
/// produced it (transactional outbox, CLAUDE.md rule 8).
/// </summary>
/// <remarks>
/// Append-only, except for the delivery bookkeeping (<see cref="Attempts"/>, <see cref="DispatchedAt"/>,
/// <see cref="LastErrorCode"/>), the only fields that change after insert (ARCHITECTURE 2.2).
/// </remarks>
internal sealed record OutboxMessage : IAppendOnlyDocument
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>The event's <c>EventId</c>, so one event can be stored only once.</summary>
    public required Guid Id { get; init; }

    public required OwnerId OwnerId { get; init; }

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required DateTimeOffset RecordedAt { get; init; }

    /// <summary>The stable event name from <c>[EventName]</c>, never the CLR type name.</summary>
    public required string EventName { get; init; }

    /// <summary>The event in the storage format.</summary>
    public required BsonDocument Payload { get; init; }

    /// <summary>How many delivery attempts failed.</summary>
    public int Attempts { get; init; }

    /// <summary>When the event was delivered; <c>null</c> while it is pending.</summary>
    public DateTimeOffset? DispatchedAt { get; init; }

    /// <summary>The error code of the last failed attempt (never a message: messages may hold data).</summary>
    public string? LastErrorCode { get; init; }
}
