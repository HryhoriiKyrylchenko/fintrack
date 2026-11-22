using FinTrack.Budget.Domain.Plans;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Shared.Kernel.Time;
using FluentResults;

namespace FinTrack.Budget.Domain.Journal;

/// <summary>
/// A new version of a plan's monthly amount (ARCHITECTURE 4.5, type name <c>PlanAmountChanged</c>, plan 1b B7): from
/// <see cref="From"/> on, the plan accrues <see cref="Amount"/> until a later version starts. A month's amount is the
/// version with the latest <c>from</c> at or before it; of two versions with the same <c>from</c>, the later one in
/// replay order. Moves no money; a definition entry (invariant 6: a version from a month before the current one that
/// changes its amount is refused).
/// </summary>
/// <remarks>Whether the plan exists, has not ended, has the amount's currency and covers <see cref="From"/> is checked by the caller.</remarks>
public sealed class PlanAmountChanged : JournalEntry, IDefinitionEntry
{
    /// <summary>The stored shape's version.</summary>
    public const int CurrentSchemaVersion = 1;

    // The mapping restores stored entries through this constructor; its parameter names match the properties.
    private PlanAmountChanged(
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
        Money amount,
        YearMonth from)
        : base(id, ownerId, schemaVersion, recordedAt, occurredOn, note, tags, groupId, source, externalId)
    {
        PlanId = planId;
        Amount = amount;
        From = from;
    }

    /// <summary>The plan.</summary>
    public PlanId PlanId { get; }

    /// <summary>The new monthly amount, above zero, in the plan's currency.</summary>
    public Money Amount { get; }

    /// <summary>The first month of the new amount.</summary>
    public YearMonth From { get; }

    /// <summary>
    /// A new version. Fails, reporting every broken rule, with <see cref="ErrorCodes.MoneyAmountNotPositive"/> (400) or
    /// <see cref="BudgetErrorCodes.EntryDateInFuture"/> (422).
    /// </summary>
    /// <param name="id">The client-generated ID.</param>
    /// <param name="ownerId">The owner.</param>
    /// <param name="planId">The plan.</param>
    /// <param name="amount">The new monthly amount.</param>
    /// <param name="from">Its first month.</param>
    /// <param name="occurredOn">The day the owner made the change.</param>
    /// <param name="today">Today in the owner's time zone.</param>
    /// <param name="info">Note, tags and source.</param>
    /// <param name="recordedAt">Now, from the clock.</param>
    public static Result<PlanAmountChanged> Create(
        JournalEntryId id,
        OwnerId ownerId,
        PlanId planId,
        Money amount,
        YearMonth from,
        DateOnly occurredOn,
        DateOnly today,
        EntryInfo info,
        DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentNullException.ThrowIfNull(info);

        var errors = new List<IError>();
        if (amount.Amount <= 0m)
        {
            errors.Add(AppError.Validation(ErrorCodes.MoneyAmountNotPositive));
        }

        if (occurredOn > today)
        {
            errors.Add(AppError.BusinessRule(BudgetErrorCodes.EntryDateInFuture));
        }

        return errors.Count > 0
            ? Result.Fail<PlanAmountChanged>(errors)
            : Result.Ok(new PlanAmountChanged(id.Value, ownerId, CurrentSchemaVersion, recordedAt, occurredOn, info.Note, info.Tags, info.GroupId, info.Source, externalId: null,
                planId, amount, from));
    }

    /// <inheritdoc />
    public override bool HasSameContent(JournalEntry other) =>
        HasSameCommonContent(other)
        && other is PlanAmountChanged changed
        && PlanId == changed.PlanId
        && Amount == changed.Amount
        && From == changed.From;
}
