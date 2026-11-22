using FinTrack.Budget.Domain.Plans;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Time;
using FluentResults;

namespace FinTrack.Budget.Domain.Journal;

/// <summary>
/// New values for a pause (ARCHITECTURE 4.5, type name <c>PlanPauseChanged</c>, plan 1b B10): its months and mode, replacing the
/// pause's (the latest change in replay order wins; earlier ones stay in the journal). Resuming is a change that sets
/// <see cref="To"/>. Moves no money; a definition entry (invariant 6).
/// </summary>
/// <remarks>Whether the pause exists for the plan and overlaps no other pause of it is checked by the caller.</remarks>
public sealed class PlanPauseChanged : JournalEntry, IDefinitionEntry
{
    /// <summary>The stored shape's version.</summary>
    public const int CurrentSchemaVersion = 1;

    // The mapping restores stored entries through this constructor; its parameter names match the properties.
    private PlanPauseChanged(
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
        PauseId pauseId,
        YearMonth from,
        YearMonth? to,
        PauseMode mode)
        : base(id, ownerId, schemaVersion, recordedAt, occurredOn, note, tags, groupId, source, externalId)
    {
        PauseId = pauseId;
        From = from;
        To = to;
        Mode = mode;
    }

    /// <summary>The pause: its <c>PlanPaused</c> entry's ID.</summary>
    public PauseId PauseId { get; }

    /// <summary>The first paused month.</summary>
    public YearMonth From { get; }

    /// <summary>The last paused month, or <c>null</c> while the pause lasts until changed.</summary>
    public YearMonth? To { get; }

    /// <summary>Shift or skip.</summary>
    public PauseMode Mode { get; }

    /// <summary>
    /// A change. Fails, reporting every broken rule, with <see cref="BudgetErrorCodes.PlanMonthsInvalid"/> (400) or
    /// <see cref="BudgetErrorCodes.EntryDateInFuture"/> (422).
    /// </summary>
    /// <param name="id">The client-generated ID.</param>
    /// <param name="ownerId">The owner.</param>
    /// <param name="pauseId">The pause.</param>
    /// <param name="from">The first paused month.</param>
    /// <param name="to">The last paused month, or <c>null</c>.</param>
    /// <param name="mode">Shift or skip.</param>
    /// <param name="occurredOn">The day the owner made the change.</param>
    /// <param name="today">Today in the owner's time zone.</param>
    /// <param name="info">Note, tags and source.</param>
    /// <param name="recordedAt">Now, from the clock.</param>
    /// <exception cref="ArgumentOutOfRangeException">The mode is not defined (a bug).</exception>
    public static Result<PlanPauseChanged> Create(
        JournalEntryId id,
        OwnerId ownerId,
        PauseId pauseId,
        YearMonth from,
        YearMonth? to,
        PauseMode mode,
        DateOnly occurredOn,
        DateOnly today,
        EntryInfo info,
        DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(info);
        var check = PlanPaused.CheckPause(from, to, mode, occurredOn, today);
        return check.IsFailed
            ? check.ToResult<PlanPauseChanged>()
            : Result.Ok(new PlanPauseChanged(id.Value, ownerId, CurrentSchemaVersion, recordedAt, occurredOn, info.Note, info.Tags, info.GroupId, info.Source, externalId: null,
                pauseId, from, to, mode));
    }

    /// <inheritdoc />
    public override bool HasSameContent(JournalEntry other) =>
        HasSameCommonContent(other)
        && other is PlanPauseChanged changed
        && PauseId == changed.PauseId
        && From == changed.From
        && To == changed.To
        && Mode == changed.Mode;
}
