using FinTrack.Budget.Contracts.V1.Plans;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Time;

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
/// <param name="BucketId">
/// The bucket of an <c>OpeningBalance</c>, <c>Deposit</c>, <c>Spend</c> or <c>Adjustment</c>; the plan's bucket of a <c>PlanCreated</c>,
/// <c>PlanAmountChanged</c>, <c>PlanEndChanged</c>, <c>PlanPaused</c>, <c>PlanPauseChanged</c> or <c>ShortfallWaived</c>; <c>null</c> for other types.
/// </param>
/// <param name="Amount">
/// The amount of an <c>OpeningBalance</c>, <c>Deposit</c>, <c>Spend</c> or <c>Adjustment</c>, in its bucket's currency; the principal of a
/// <c>Borrow</c> and the amount of a <c>Repayment</c>, in the lender's currency; the monthly amount of a <c>PlanCreated</c> or
/// <c>PlanAmountChanged</c> (an amount the plan asks for, not money moved); what a <c>ShortfallWaived</c> writes off (not money moved either);
/// <c>null</c> for other types.
/// </param>
/// <param name="FromBucketId">
/// The source of a <c>Transfer</c>; the lender of a <c>Borrow</c>; the source of a <c>Repayment</c>, or <c>null</c> when it was paid with
/// new money from outside; <c>null</c> for other types.
/// </param>
/// <param name="ToBucketId">
/// The destination of a <c>Transfer</c>; the destination of a <c>Borrow</c>, or <c>null</c> when the money left the app; the lender of a
/// <c>Repayment</c>; <c>null</c> for other types.
/// </param>
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
/// <param name="LoanId">The loan of a <c>Borrow</c> (its own ID) or a <c>Repayment</c>; <c>null</c> for other types (plan 1b B18).</param>
/// <param name="PlanId">
/// The plan of a <c>PlanCreated</c> (its own ID), <c>PlanAmountChanged</c>, <c>PlanEndChanged</c>, <c>PlanPaused</c>, <c>PlanPauseChanged</c>
/// or <c>ShortfallWaived</c>, and the plan a <c>Deposit</c> or a
/// <c>Transfer</c> counts towards (or <c>null</c> for a one-time contribution); <c>null</c> for other types (plan 1b B9, B18).
/// </param>
/// <param name="FromMonth">
/// The first month of a <c>PlanCreated</c>, of a <c>PlanAmountChanged</c>'s amount, or of a pause (<c>PlanPaused</c>, <c>PlanPauseChanged</c>);
/// <c>null</c> for other types.
/// </param>
/// <param name="ToMonth">
/// The last month of a <c>PlanCreated</c>, a <c>PlanEndChanged</c> or a pause, or <c>null</c> when there is none (no end, a pause until
/// changed); <c>null</c> for other types.
/// </param>
/// <param name="PauseId">The pause of a <c>PlanPaused</c> (its own ID) or a <c>PlanPauseChanged</c>; <c>null</c> for other types (plan 1b B18).</param>
/// <param name="PauseMode">The mode of a <c>PlanPaused</c> or a <c>PlanPauseChanged</c>; <c>null</c> for other types.</param>
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
    AdjustmentDirection? Direction = null,
    Guid? LoanId = null,
    Guid? PlanId = null,
    YearMonth? FromMonth = null,
    YearMonth? ToMonth = null,
    Guid? PauseId = null,
    PauseMode? PauseMode = null);

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

    /// <summary>Money borrowed from a savings bucket with the obligation to return it; opens a loan with this entry's ID.</summary>
    Borrow,

    /// <summary>Loan principal paid back to its lender; never income.</summary>
    Repayment,

    /// <summary>A new contribution plan for one bucket (bucket, monthly amount, months); moves no money; its ID is the plan's.</summary>
    PlanCreated,

    /// <summary>A plan's new monthly amount from a month on; moves no money.</summary>
    PlanAmountChanged,

    /// <summary>A plan's new last month, or no end; moves no money.</summary>
    PlanEndChanged,

    /// <summary>A pause of a plan (months and mode); moves no money; its ID is the pause's.</summary>
    PlanPaused,

    /// <summary>New months or mode of a pause, for example a resume; moves no money.</summary>
    PlanPauseChanged,

    /// <summary>Part of a plan's shortfall written off, with its reason; moves no money and is never income.</summary>
    ShortfallWaived,
}
