using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Results;
using FluentResults;

namespace FinTrack.Budget.Domain.Journal;

/// <summary>
/// Cancels exactly one earlier entry (ARCHITECTURE 4.2, type name <c>Reversal</c>, plan 1a D3): the state
/// builder then treats the reversed entry as if it never happened, at its original date. The reversal
/// itself moves no money; its date is the day of the correction.
/// </summary>
/// <remarks>
/// Any entry except a reversal can be reversed, once; a reversal cannot be reversed (to redo, enter a new
/// entry). Reversing a transfer voids both sides. Whether the entry exists and may be reversed, and
/// whether the journal without it keeps invariant 1, is checked by the caller against the state.
/// </remarks>
public sealed class Reversal : JournalEntry
{
    /// <summary>The stored shape's version.</summary>
    public const int CurrentSchemaVersion = 1;

    // The mapping restores stored entries through this constructor; its parameter names match the properties.
    private Reversal(
        Guid id,
        OwnerId ownerId,
        int schemaVersion,
        DateTimeOffset recordedAt,
        DateOnly occurredOn,
        string? note,
        IReadOnlyList<string> tags,
        Guid? groupId,
        EntrySource source,
        string? externalId,
        JournalEntryId reversedEntryId)
        : base(id, ownerId, schemaVersion, recordedAt, occurredOn, note, tags, groupId, source, externalId) =>
        ReversedEntryId = reversedEntryId;

    /// <summary>The entry this reversal cancels.</summary>
    public JournalEntryId ReversedEntryId { get; }

    /// <summary>A new reversal. Fails with <see cref="BudgetErrorCodes.EntryDateInFuture"/> when dated after <paramref name="today"/>.</summary>
    /// <param name="id">The client-generated ID.</param>
    /// <param name="ownerId">The owner.</param>
    /// <param name="reversedEntryId">The entry to cancel.</param>
    /// <param name="occurredOn">The day of the correction.</param>
    /// <param name="today">Today in the owner's time zone.</param>
    /// <param name="info">Note, tags and source.</param>
    /// <param name="recordedAt">Now, from the clock.</param>
    public static Result<Reversal> Create(
        JournalEntryId id,
        OwnerId ownerId,
        JournalEntryId reversedEntryId,
        DateOnly occurredOn,
        DateOnly today,
        EntryInfo info,
        DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(info);

        return occurredOn > today
            ? Result.Fail<Reversal>(AppError.BusinessRule(BudgetErrorCodes.EntryDateInFuture))
            : Result.Ok(new Reversal(id.Value, ownerId, CurrentSchemaVersion, recordedAt, occurredOn, info.Note, info.Tags, groupId: null, info.Source, externalId: null, reversedEntryId));
    }

    /// <inheritdoc />
    public override bool HasSameContent(JournalEntry other) =>
        HasSameCommonContent(other)
        && other is Reversal reversal
        && ReversedEntryId == reversal.ReversedEntryId;
}
