namespace FinTrack.Budget.Domain.Journal;

/// <summary>
/// The order in which the journal is replayed (ARCHITECTURE 4.2, plan 1a D2): <c>OccurredOn</c>, then
/// <c>RecordedAt</c>, then <c>Id</c>. Entries of one commit share <c>RecordedAt</c>, so the ID breaks
/// the tie; it is compared byte by byte in its standard (big-endian) form, which is how MongoDB sorts
/// stored UUIDs, so the database and memory agree on the order.
/// </summary>
public sealed class ReplayOrder : IComparer<JournalEntry>
{
    private const int GuidLength = 16;

    private ReplayOrder()
    {
    }

    /// <summary>The comparer.</summary>
    public static ReplayOrder Instance { get; } = new();

    /// <summary>The entries in replay order (a new list; the input is not changed).</summary>
    public static IReadOnlyList<JournalEntry> Sort(IEnumerable<JournalEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return [.. entries.Order(Instance)];
    }

    /// <summary>Compares two IDs as MongoDB compares standard UUIDs: by their big-endian bytes.</summary>
    public static int CompareIds(Guid first, Guid second)
    {
        Span<byte> left = stackalloc byte[GuidLength];
        Span<byte> right = stackalloc byte[GuidLength];
        first.TryWriteBytes(left, bigEndian: true, out _);
        second.TryWriteBytes(right, bigEndian: true, out _);
        return left.SequenceCompareTo(right);
    }

    /// <inheritdoc />
    public int Compare(JournalEntry? x, JournalEntry? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);

        return ReplayPosition.Of(x).CompareTo(ReplayPosition.Of(y));
    }
}
