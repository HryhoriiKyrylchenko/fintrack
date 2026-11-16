using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Persistence;

namespace FinTrack.Shared.Infrastructure.Ledger;

/// <summary>
/// The version of one owner's ledger in one context: one small document per owner and context,
/// unique by owner. Every commit raises it by one, checked against the version read before the
/// journal, so concurrent commits conflict instead of passing on stale data (CLAUDE.md rule 7).
/// </summary>
internal sealed record LedgerHead : IMutableDocument
{
    public const int CurrentSchemaVersion = 1;

    public required Guid Id { get; init; }

    public required OwnerId OwnerId { get; init; }

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public long Version { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
