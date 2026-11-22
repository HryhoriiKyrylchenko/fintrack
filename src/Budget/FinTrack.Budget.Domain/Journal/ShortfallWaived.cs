using FinTrack.Budget.Domain.Plans;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FluentResults;

namespace FinTrack.Budget.Domain.Journal;

/// <summary>
/// Part of a plan's shortfall written off (ARCHITECTURE 4.5, type name <c>ShortfallWaived</c>, plan 1b B11), with its reason in the
/// note. Moves no money. It settles the oldest months like a payment, but only months before its own month and only what the
/// contributions and earlier waivers left: a later backdated contribution makes the part no longer needed void, so a waiver never
/// creates a prepayment. Reversible: the shortfall returns.
/// </summary>
/// <remarks>
/// The reason is the common note, required for this type: the validator reports a missing one, so one that reaches the factory
/// without it is a bug. Whether the plan exists, has the amount's currency and the amount fits its shortfall is checked by the caller.
/// </remarks>
public sealed class ShortfallWaived : JournalEntry, IDefinitionEntry
{
    /// <summary>The stored shape's version.</summary>
    public const int CurrentSchemaVersion = 1;

    // The mapping restores stored entries through this constructor; its parameter names match the properties.
    private ShortfallWaived(
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
        Money amount)
        : base(id, ownerId, schemaVersion, recordedAt, occurredOn, note, tags, groupId, source, externalId)
    {
        PlanId = planId;
        Amount = amount;
    }

    /// <summary>The plan.</summary>
    public PlanId PlanId { get; }

    /// <summary>What is written off, above zero, in the plan's currency.</summary>
    public Money Amount { get; }

    /// <summary>
    /// A new waiver. Fails, reporting every broken rule, with <see cref="ErrorCodes.MoneyAmountNotPositive"/> (400) or
    /// <see cref="BudgetErrorCodes.EntryDateInFuture"/> (422).
    /// </summary>
    /// <param name="id">The client-generated ID.</param>
    /// <param name="ownerId">The owner.</param>
    /// <param name="planId">The plan.</param>
    /// <param name="amount">What is written off.</param>
    /// <param name="occurredOn">The day of the waiver.</param>
    /// <param name="today">Today in the owner's time zone.</param>
    /// <param name="info">Note (required: the reason), tags and source.</param>
    /// <param name="recordedAt">Now, from the clock.</param>
    /// <exception cref="ArgumentException">The note is missing (a bug: the validator requires it).</exception>
    public static Result<ShortfallWaived> Create(
        JournalEntryId id,
        OwnerId ownerId,
        PlanId planId,
        Money amount,
        DateOnly occurredOn,
        DateOnly today,
        EntryInfo info,
        DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentNullException.ThrowIfNull(info);
        if (info.Note is null)
        {
            throw new ArgumentException("A waiver needs a note: the reason.", nameof(info));
        }

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
            ? Result.Fail<ShortfallWaived>(errors)
            : Result.Ok(new ShortfallWaived(id.Value, ownerId, CurrentSchemaVersion, recordedAt, occurredOn, info.Note, info.Tags, info.GroupId, info.Source, externalId: null,
                planId, amount));
    }

    /// <inheritdoc />
    public override bool HasSameContent(JournalEntry other) =>
        HasSameCommonContent(other)
        && other is ShortfallWaived waived
        && PlanId == waived.PlanId
        && Amount == waived.Amount;
}
