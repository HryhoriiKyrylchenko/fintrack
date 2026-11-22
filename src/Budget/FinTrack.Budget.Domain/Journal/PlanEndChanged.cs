using FinTrack.Budget.Domain.Plans;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Shared.Kernel.Time;
using FluentResults;

namespace FinTrack.Budget.Domain.Journal;

/// <summary>
/// A plan's new last accruing month (ARCHITECTURE 4.5, type name <c>PlanEndChanged</c>, plan 1b B7), or no end when
/// <see cref="ValidTo"/> is <c>null</c>. Of several, the latest in replay order wins. The end stops new periods, not the
/// obligation: a shortfall stays until paid or waived. Moves no money; a definition entry (invariant 6: an end that
/// removes or adds a month before the current one is refused).
/// </summary>
/// <remarks>Whether the plan exists and the end is not before its start is checked by the caller.</remarks>
public sealed class PlanEndChanged : JournalEntry, IDefinitionEntry
{
    /// <summary>The stored shape's version.</summary>
    public const int CurrentSchemaVersion = 1;

    // The mapping restores stored entries through this constructor; its parameter names match the properties.
    private PlanEndChanged(
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
        YearMonth? validTo)
        : base(id, ownerId, schemaVersion, recordedAt, occurredOn, note, tags, groupId, source, externalId)
    {
        PlanId = planId;
        ValidTo = validTo;
    }

    /// <summary>The plan.</summary>
    public PlanId PlanId { get; }

    /// <summary>The new last accruing month, or <c>null</c> to remove the end.</summary>
    public YearMonth? ValidTo { get; }

    /// <summary>A new end. Fails with <see cref="BudgetErrorCodes.EntryDateInFuture"/> (422).</summary>
    /// <param name="id">The client-generated ID.</param>
    /// <param name="ownerId">The owner.</param>
    /// <param name="planId">The plan.</param>
    /// <param name="validTo">The last month, or <c>null</c>.</param>
    /// <param name="occurredOn">The day the owner made the change.</param>
    /// <param name="today">Today in the owner's time zone.</param>
    /// <param name="info">Note, tags and source.</param>
    /// <param name="recordedAt">Now, from the clock.</param>
    public static Result<PlanEndChanged> Create(
        JournalEntryId id,
        OwnerId ownerId,
        PlanId planId,
        YearMonth? validTo,
        DateOnly occurredOn,
        DateOnly today,
        EntryInfo info,
        DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(info);

        return occurredOn > today
            ? Result.Fail<PlanEndChanged>(AppError.BusinessRule(BudgetErrorCodes.EntryDateInFuture))
            : Result.Ok(new PlanEndChanged(id.Value, ownerId, CurrentSchemaVersion, recordedAt, occurredOn, info.Note, info.Tags, info.GroupId, info.Source, externalId: null,
                planId, validTo));
    }

    /// <inheritdoc />
    public override bool HasSameContent(JournalEntry other) =>
        HasSameCommonContent(other)
        && other is PlanEndChanged changed
        && PlanId == changed.PlanId
        && ValidTo == changed.ValidTo;
}
