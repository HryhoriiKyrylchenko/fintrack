using FinTrack.Budget.Domain.Plans;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Shared.Kernel.Time;
using FluentResults;

namespace FinTrack.Budget.Domain.Journal;

/// <summary>
/// A pause of a plan (ARCHITECTURE 4.5, type name <c>PlanPaused</c>, plan 1b B10): the months from <see cref="From"/> to
/// <see cref="To"/> (inclusive; without <c>to</c>, until changed) accrue nothing; <see cref="Mode"/> decides whether the end moves
/// (<see cref="PauseMode.Shift"/>) or stays (<see cref="PauseMode.Skip"/>). Its ID is the pause's ID (<see cref="PauseId"/>).
/// Moves no money; a definition entry (invariant 6: a pause that changes a month before the current one is refused).
/// </summary>
/// <remarks>Whether the plan exists, has not ended and has no other pause overlapping these months is checked by the caller.</remarks>
public sealed class PlanPaused : JournalEntry, IDefinitionEntry
{
    /// <summary>The stored shape's version.</summary>
    public const int CurrentSchemaVersion = 1;

    // The mapping restores stored entries through this constructor; its parameter names match the properties.
    private PlanPaused(
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
        PlanId planId,
        YearMonth from,
        YearMonth? to,
        PauseMode mode)
        : base(id, ownerId, schemaVersion, recordedAt, occurredOn, note, tags, groupId, source, externalId)
    {
        PlanId = planId;
        From = from;
        To = to;
        Mode = mode;
    }

    /// <summary>The pause this entry starts.</summary>
    public PauseId PauseId => PauseId.From(Id);

    /// <summary>The plan.</summary>
    public PlanId PlanId { get; }

    /// <summary>The first paused month.</summary>
    public YearMonth From { get; }

    /// <summary>The last paused month, or <c>null</c> while the pause lasts until changed.</summary>
    public YearMonth? To { get; }

    /// <summary>Whether the end moves by the paused months or stays.</summary>
    public PauseMode Mode { get; }

    /// <summary>
    /// A new pause. Fails, reporting every broken rule, with <see cref="BudgetErrorCodes.PlanMonthsInvalid"/> (400, a last month
    /// before the first) or <see cref="BudgetErrorCodes.EntryDateInFuture"/> (422).
    /// </summary>
    /// <param name="id">The client-generated ID; also the pause's ID.</param>
    /// <param name="ownerId">The owner.</param>
    /// <param name="planId">The plan.</param>
    /// <param name="from">The first paused month.</param>
    /// <param name="to">The last paused month, or <c>null</c>.</param>
    /// <param name="mode">Shift or skip.</param>
    /// <param name="occurredOn">The day the owner made the pause.</param>
    /// <param name="today">Today in the owner's time zone.</param>
    /// <param name="info">Note, tags and source.</param>
    /// <param name="recordedAt">Now, from the clock.</param>
    /// <exception cref="ArgumentOutOfRangeException">The mode is not defined (a bug).</exception>
    public static Result<PlanPaused> Create(
        JournalEntryId id,
        OwnerId ownerId,
        PlanId planId,
        YearMonth from,
        YearMonth? to,
        PauseMode mode,
        DateOnly occurredOn,
        DateOnly today,
        EntryInfo info,
        DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(info);
        var check = CheckPause(from, to, mode, occurredOn, today);
        return check.IsFailed
            ? check.ToResult<PlanPaused>()
            : Result.Ok(new PlanPaused(id.Value, ownerId, CurrentSchemaVersion, recordedAt, occurredOn, info.Note, info.Tags, info.GroupId, info.Source, externalId: null,
                planId, from, to, mode));
    }

    /// <inheritdoc />
    public override bool HasSameContent(JournalEntry other) =>
        HasSameCommonContent(other)
        && other is PlanPaused paused
        && PlanId == paused.PlanId
        && From == paused.From
        && To == paused.To
        && Mode == paused.Mode;

    /// <summary>The rules a pause and a change of one share: months in order and a date not after today; every one reported.</summary>
    internal static Result CheckPause(YearMonth from, YearMonth? to, PauseMode mode, DateOnly occurredOn, DateOnly today)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown pause mode.");
        }

        var errors = new List<IError>();
        if (to < from)
        {
            errors.Add(AppError.Validation(BudgetErrorCodes.PlanMonthsInvalid));
        }

        if (occurredOn > today)
        {
            errors.Add(AppError.BusinessRule(BudgetErrorCodes.EntryDateInFuture));
        }

        return errors.Count > 0 ? Result.Fail(errors) : Result.Ok();
    }
}
