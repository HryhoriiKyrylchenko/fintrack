using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Persistence;

namespace FinTrack.Budget.Domain.Journal;

/// <summary>
/// One entry of the Budget journal (ARCHITECTURE 4.2): append-only, never changed or deleted; a
/// mistake is corrected by a reversal. Each entry type is its own sealed class (plan 1a D1); all are
/// stored in <c>budget_journal</c> with a stable <c>type</c> name set in the mapping.
/// </summary>
/// <remarks>
/// Entry types are classes, not records: a record would add a copy constructor and <c>with</c>, a
/// second way to build an entry that bypasses its factory's rules. Comparison for a repeated command
/// is explicit (<see cref="HasSameContent"/>).
/// </remarks>
public abstract class JournalEntry : IAppendOnlyDocument
{
    private protected JournalEntry(
        Guid id,
        OwnerId ownerId,
        int schemaVersion,
        DateTimeOffset recordedAt,
        DateOnly occurredOn,
        string? note,
        IReadOnlyList<string> tags,
        Guid? groupId,
        EntrySource source,
        string? externalId)
    {
        Id = id;
        OwnerId = ownerId;
        SchemaVersion = schemaVersion;
        RecordedAt = recordedAt;
        OccurredOn = occurredOn;
        Note = note;
        Tags = tags;
        GroupId = groupId;
        Source = source;
        ExternalId = externalId;
    }

    /// <inheritdoc />
    public Guid Id { get; }

    /// <summary>The identifier as its own type.</summary>
    public JournalEntryId EntryId => JournalEntryId.From(Id);

    /// <inheritdoc />
    public OwnerId OwnerId { get; }

    /// <inheritdoc />
    public int SchemaVersion { get; }

    /// <inheritdoc />
    public DateTimeOffset RecordedAt { get; }

    /// <summary>The business date, chosen by the user; never after today in the owner's time zone.</summary>
    public DateOnly OccurredOn { get; }

    /// <summary>A note, or <c>null</c>.</summary>
    public string? Note { get; }

    /// <summary>Tags, possibly none.</summary>
    public IReadOnlyList<string> Tags { get; }

    /// <summary>
    /// Links the entries one command writes together (plan 1b B15), client-generated; <c>null</c> for an
    /// entry written alone. Part of the content a repeat must match.
    /// </summary>
    public Guid? GroupId { get; }

    /// <summary>Where the entry came from.</summary>
    public EntrySource Source { get; }

    /// <summary>The ID at the source of an import, for recognising duplicates (later phases); <c>null</c> otherwise.</summary>
    public string? ExternalId { get; }

    /// <summary>
    /// Whether <paramref name="other"/> is the same entry in everything except <see cref="RecordedAt"/>:
    /// what a repeated command would write (invariant 13, plan 1a D4). Texts are compared exactly.
    /// </summary>
    public abstract bool HasSameContent(JournalEntry other);

    /// <summary>The comparison of the common fields, for <see cref="HasSameContent"/>.</summary>
    private protected bool HasSameCommonContent(JournalEntry other) =>
        other is not null
        && other.GetType() == GetType()
        && Id == other.Id
        && OwnerId == other.OwnerId
        && SchemaVersion == other.SchemaVersion
        && OccurredOn == other.OccurredOn
        && string.Equals(Note, other.Note, StringComparison.Ordinal)
        && Tags.SequenceEqual(other.Tags, StringComparer.Ordinal)
        && GroupId == other.GroupId
        && Source == other.Source
        && string.Equals(ExternalId, other.ExternalId, StringComparison.Ordinal);
}
