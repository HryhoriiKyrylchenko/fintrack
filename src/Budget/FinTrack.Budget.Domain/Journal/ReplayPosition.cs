namespace FinTrack.Budget.Domain.Journal;

/// <summary>
/// Where an entry stands in the replay order (<see cref="ReplayOrder"/>): its business date, its recorded
/// time and its ID. Comparing two positions is the replay order itself, so a position kept apart from its
/// entry (a paging cursor) compares exactly as the entry would.
/// </summary>
/// <param name="OccurredOn">The entry's business date.</param>
/// <param name="RecordedAt">The entry's recorded time.</param>
/// <param name="Id">The entry's ID, compared by its big-endian bytes (<see cref="ReplayOrder.CompareIds"/>).</param>
public readonly record struct ReplayPosition(DateOnly OccurredOn, DateTimeOffset RecordedAt, Guid Id) : IComparable<ReplayPosition>
{
    /// <summary>The position of <paramref name="entry"/>.</summary>
    public static ReplayPosition Of(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new ReplayPosition(entry.OccurredOn, entry.RecordedAt, entry.Id);
    }

    /// <summary>Earlier in the replay order is less: by date, then recorded time, then ID.</summary>
    public int CompareTo(ReplayPosition other)
    {
        var byDate = OccurredOn.CompareTo(other.OccurredOn);
        if (byDate != 0)
        {
            return byDate;
        }

        var byRecordedAt = RecordedAt.CompareTo(other.RecordedAt);
        return byRecordedAt != 0 ? byRecordedAt : ReplayOrder.CompareIds(Id, other.Id);
    }

    /// <summary>Whether <paramref name="left"/> comes before <paramref name="right"/>.</summary>
    public static bool operator <(ReplayPosition left, ReplayPosition right) => left.CompareTo(right) < 0;

    /// <summary>Whether <paramref name="left"/> comes after <paramref name="right"/>.</summary>
    public static bool operator >(ReplayPosition left, ReplayPosition right) => left.CompareTo(right) > 0;

    /// <summary>Whether <paramref name="left"/> comes before or is <paramref name="right"/>.</summary>
    public static bool operator <=(ReplayPosition left, ReplayPosition right) => left.CompareTo(right) <= 0;

    /// <summary>Whether <paramref name="left"/> comes after or is <paramref name="right"/>.</summary>
    public static bool operator >=(ReplayPosition left, ReplayPosition right) => left.CompareTo(right) >= 0;
}
