using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Plans;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Shared.Kernel.Time;
using FluentResults;

namespace FinTrack.Budget.Domain.Journal;

/// <summary>
/// A new contribution plan for one bucket (ARCHITECTURE 4.5, type name <c>PlanCreated</c>, plan 1b B1, B7): a monthly
/// amount from <see cref="ValidFrom"/> to <see cref="ValidTo"/> (or without an end). Its ID is the plan's ID
/// (<see cref="PlanId"/>). Moves no money; it is a definition entry (invariant 6).
/// </summary>
/// <remarks>
/// <para>
/// A plan may start in the past (catching up: no period exists yet to change) or in a later month. Its first amount is
/// its first version, from <see cref="ValidFrom"/>; <see cref="PlanAmountChanged"/> adds versions and
/// <see cref="PlanEndChanged"/> moves the end.
/// </para>
/// <para>
/// Whether the bucket exists, is an active user's bucket (not <c>Unallocated</c>) and has the amount's currency is checked
/// by the caller against the state.
/// </para>
/// </remarks>
public sealed class PlanCreated : JournalEntry, IDefinitionEntry
{
    /// <summary>The stored shape's version.</summary>
    public const int CurrentSchemaVersion = 1;

    // The mapping restores stored entries through this constructor; its parameter names match the properties.
    private PlanCreated(
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
        BucketId bucketId,
        Money amount,
        YearMonth validFrom,
        YearMonth? validTo)
        : base(id, ownerId, schemaVersion, recordedAt, occurredOn, note, tags, groupId, source, externalId)
    {
        BucketId = bucketId;
        Amount = amount;
        ValidFrom = validFrom;
        ValidTo = validTo;
    }

    /// <summary>The plan this entry creates.</summary>
    public PlanId PlanId => PlanId.From(Id);

    /// <summary>The bucket the plan saves into.</summary>
    public BucketId BucketId { get; }

    /// <summary>The monthly amount, above zero, in the bucket's currency: the first version.</summary>
    public Money Amount { get; }

    /// <summary>The first month that accrues.</summary>
    public YearMonth ValidFrom { get; }

    /// <summary>The last month that accrues, or <c>null</c> for a plan without an end.</summary>
    public YearMonth? ValidTo { get; }

    /// <summary>
    /// A new plan. Fails, reporting every broken rule, with <see cref="ErrorCodes.MoneyAmountNotPositive"/>,
    /// <see cref="BudgetErrorCodes.PlanMonthsInvalid"/> (an end before the start; both 400) or
    /// <see cref="BudgetErrorCodes.EntryDateInFuture"/> (422).
    /// </summary>
    /// <param name="id">The client-generated ID; also the plan's ID.</param>
    /// <param name="ownerId">The owner.</param>
    /// <param name="bucketId">The bucket.</param>
    /// <param name="amount">The monthly amount.</param>
    /// <param name="validFrom">The first month.</param>
    /// <param name="validTo">The last month, or <c>null</c>.</param>
    /// <param name="occurredOn">The day the owner made the plan.</param>
    /// <param name="today">Today in the owner's time zone.</param>
    /// <param name="info">Note, tags and source.</param>
    /// <param name="recordedAt">Now, from the clock.</param>
    public static Result<PlanCreated> Create(
        JournalEntryId id,
        OwnerId ownerId,
        BucketId bucketId,
        Money amount,
        YearMonth validFrom,
        YearMonth? validTo,
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

        if (validTo < validFrom)
        {
            errors.Add(AppError.Validation(BudgetErrorCodes.PlanMonthsInvalid));
        }

        if (occurredOn > today)
        {
            errors.Add(AppError.BusinessRule(BudgetErrorCodes.EntryDateInFuture));
        }

        return errors.Count > 0
            ? Result.Fail<PlanCreated>(errors)
            : Result.Ok(new PlanCreated(id.Value, ownerId, CurrentSchemaVersion, recordedAt, occurredOn, info.Note, info.Tags, info.GroupId, info.Source, externalId: null,
                bucketId, amount, validFrom, validTo));
    }

    /// <inheritdoc />
    public override bool HasSameContent(JournalEntry other) =>
        HasSameCommonContent(other)
        && other is PlanCreated created
        && BucketId == created.BucketId
        && Amount == created.Amount
        && ValidFrom == created.ValidFrom
        && ValidTo == created.ValidTo;
}
