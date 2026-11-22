using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Plans;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FluentResults;

namespace FinTrack.Budget.Domain.Journal;

/// <summary>
/// New money into one bucket (ARCHITECTURE 4.2, type name <c>Deposit</c>). Raises the bucket's
/// <c>NetContributed</c>; the only entry that counts as income (invariant 5).
/// </summary>
/// <remarks>
/// A deposit may count towards a contribution plan of its bucket (<see cref="PlanId"/>, plan 1b B9); whether the plan
/// exists and belongs to the bucket is checked by the caller against the state.
/// </remarks>
public sealed class Deposit : SingleBucketEntry
{
    /// <summary>The stored shape's version.</summary>
    public const int CurrentSchemaVersion = 1;

    // The mapping restores stored entries through this constructor; its parameter names match the properties.
    private Deposit(
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
        PlanId? planId)
        : base(id, ownerId, schemaVersion, recordedAt, occurredOn, note, tags, groupId, source, externalId, bucketId, amount)
    {
        PlanId = planId;
    }

    /// <summary>The contribution plan this deposit counts towards (plan 1b B9), or <c>null</c> for a one-time deposit.</summary>
    public PlanId? PlanId { get; }

    /// <summary>
    /// A new deposit. Fails with <see cref="ErrorCodes.MoneyAmountNotPositive"/> and/or
    /// <see cref="BudgetErrorCodes.EntryDateInFuture"/> (after <paramref name="today"/>, the owner's
    /// date). Whether the bucket exists, is active and has the amount's currency is checked by the
    /// caller against the state.
    /// </summary>
    /// <param name="id">The client-generated ID.</param>
    /// <param name="ownerId">The owner.</param>
    /// <param name="bucketId">The receiving bucket.</param>
    /// <param name="amount">The amount.</param>
    /// <param name="occurredOn">The business date.</param>
    /// <param name="today">Today in the owner's time zone.</param>
    /// <param name="info">Note, tags and source.</param>
    /// <param name="recordedAt">Now, from the clock.</param>
    /// <param name="planId">The contribution plan it counts towards, or <c>null</c>.</param>
    public static Result<Deposit> Create(
        JournalEntryId id,
        OwnerId ownerId,
        BucketId bucketId,
        Money amount,
        DateOnly occurredOn,
        DateOnly today,
        EntryInfo info,
        DateTimeOffset recordedAt,
        PlanId? planId = null)
    {
        var check = Check(amount, occurredOn, today, info);
        return check.IsFailed
            ? check.ToResult<Deposit>()
            : Result.Ok(new Deposit(id.Value, ownerId, CurrentSchemaVersion, recordedAt, occurredOn, info.Note, info.Tags, info.GroupId, info.Source, externalId: null, bucketId, amount, planId));
    }

    /// <inheritdoc />
    public override bool HasSameContent(JournalEntry other) =>
        base.HasSameContent(other)
        && other is Deposit deposit
        && PlanId == deposit.PlanId;
}
