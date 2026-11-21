using FinTrack.Shared.Kernel.Monetary;

namespace FinTrack.Budget.Contracts.V1.Journal;

/// <summary>The answer to <see cref="GetJournal"/>, version 1 of the contract.</summary>
/// <param name="Entries">At most <see cref="GetJournal.Limit"/> entries that match the filters, newest first; empty when none is left.</param>
/// <param name="NextCursor">Pass it as <see cref="GetJournal.Cursor"/> (with the same filters) for the next page; <c>null</c> when this page is the last.</param>
public sealed record JournalPageDto(IReadOnlyList<JournalEntryDto> Entries, string? NextCursor);

/// <summary>
/// One journal entry (ARCHITECTURE 4.2) as stored, version 1 of the contract. One shape for every type: the
/// fields of other types are <c>null</c>. Amounts are always above zero; <see cref="Type"/> gives the direction, and for an
/// <c>Adjustment</c> <see cref="Direction"/> does. Within V1 the shape grows only by new properties at the end (plan 1b B18).
/// </summary>
/// <param name="Id">The entry's ID (a GUID string in JSON).</param>
/// <param name="Type">What the entry does; decides which of the fields below are set.</param>
/// <param name="OccurredOn">The business date chosen by the user (never after the day it was recorded, in the owner's time zone). For a reversal, the day of the correction.</param>
/// <param name="RecordedAt">When the system recorded the entry (UTC). Entries written together share it.</param>
/// <param name="Note">The note, or <c>null</c>.</param>
/// <param name="Tags">The tags, possibly none.</param>
/// <param name="GroupId">The entries created by one user action share it; <c>null</c> for an entry on its own (always in phase 1a).</param>
/// <param name="Source">Where the entry came from: <c>Manual</c>, <c>Import:&lt;connector&gt;</c> or <c>Agent:&lt;name&gt;</c>.</param>
/// <param name="ExternalId">The source's own ID of an imported entry, or <c>null</c> (always in phase 1a).</param>
/// <param name="BucketId">The bucket of an <c>OpeningBalance</c>, <c>Deposit</c>, <c>Spend</c> or <c>Adjustment</c>; <c>null</c> for other types.</param>
/// <param name="Amount">The amount of an <c>OpeningBalance</c>, <c>Deposit</c>, <c>Spend</c> or <c>Adjustment</c>, in its bucket's currency; <c>null</c> for other types.</param>
/// <param name="FromBucketId">The source of a <c>Transfer</c>; <c>null</c> for other types.</param>
/// <param name="ToBucketId">The destination of a <c>Transfer</c>; <c>null</c> for other types.</param>
/// <param name="FromAmount">Everything that left the source of a <c>Transfer</c>, fee included, in the source's currency; <c>null</c> for other types.</param>
/// <param name="ToAmount">What arrived at the destination of a <c>Transfer</c>, in its currency; <c>null</c> for other types.</param>
/// <param name="Fee">The fee of a <c>Transfer</c> (part of <paramref name="FromAmount"/>, in the source's currency); <c>null</c> when there was none and for other types.</param>
/// <param name="Rate">
/// A <c>Transfer</c>'s rate, derived from its amounts: <paramref name="ToAmount"/> ÷ (<paramref name="FromAmount"/> − <paramref name="Fee"/>),
/// 12 decimal places at most; 1 within one currency. <c>null</c> for other types. For display; the amounts are the truth.
/// </param>
/// <param name="ReversedEntryId">The entry a <c>Reversal</c> cancels; <c>null</c> for other types.</param>
/// <param name="ReversedBy">
/// The ID of the <c>Reversal</c> that cancels this entry, or <c>null</c> while it counts. A reversed entry counts
/// as if it never happened, at its original date.
/// </param>
/// <param name="Direction">Whether an <c>Adjustment</c> raised or lowered its bucket; <c>null</c> for other types (plan 1b B18).</param>
public sealed record JournalEntryDto(
    Guid Id,
    JournalEntryType Type,
    DateOnly OccurredOn,
    DateTimeOffset RecordedAt,
    string? Note,
    IReadOnlyList<string> Tags,
    Guid? GroupId,
    string Source,
    string? ExternalId,
    Guid? BucketId,
    Money? Amount,
    Guid? FromBucketId,
    Guid? ToBucketId,
    Money? FromAmount,
    Money? ToAmount,
    Money? Fee,
    decimal? Rate,
    Guid? ReversedEntryId,
    Guid? ReversedBy,
    AdjustmentDirection? Direction = null);

/// <summary>The type of a journal entry (ARCHITECTURE 4.2). JSON: the member's name, which is also the stored type name.</summary>
public enum JournalEntryType
{
    /// <summary>The starting amount of a bucket; not income.</summary>
    OpeningBalance,

    /// <summary>New money into a bucket; the only income.</summary>
    Deposit,

    /// <summary>Money that left for good.</summary>
    Spend,

    /// <summary>Money moved between two buckets, possibly across currencies; never income.</summary>
    Transfer,

    /// <summary>Cancels one earlier entry; moves no money itself.</summary>
    Reversal,

    /// <summary>A manual correction of one bucket, up or down, with a reason (the note); never income.</summary>
    Adjustment,
}
