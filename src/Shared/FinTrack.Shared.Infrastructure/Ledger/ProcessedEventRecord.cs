using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Persistence;

namespace FinTrack.Shared.Infrastructure.Ledger;

/// <summary>
/// Records that a handler handled an event; written in the handler's own commit and unique by
/// owner, handler and event, so a repeated delivery is recognised (invariant 13).
/// </summary>
internal sealed record ProcessedEventRecord : IAppendOnlyDocument
{
    public const int CurrentSchemaVersion = 1;

    public required Guid Id { get; init; }

    public required OwnerId OwnerId { get; init; }

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required DateTimeOffset RecordedAt { get; init; }

    /// <summary>Stable name of the handler, never its CLR type name.</summary>
    public required string HandlerName { get; init; }

    /// <summary>The handled event.</summary>
    public required Guid EventId { get; init; }
}
