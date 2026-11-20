using FinTrack.Budget.Domain.Buckets;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FluentResults;

namespace FinTrack.Budget.Domain.Journal;

/// <summary>
/// The amount a bucket held when the owner started using the app (ARCHITECTURE 4.2, type name
/// <c>OpeningBalance</c>). Raises <c>NetContributed</c> but is not income. Usually backdated. A bucket has
/// at most one that is not reversed (<see cref="BudgetErrorCodes.BucketOpeningBalanceExists"/>, D13).
/// </summary>
public sealed class OpeningBalance : SingleBucketEntry
{
    /// <summary>The stored shape's version.</summary>
    public const int CurrentSchemaVersion = 1;

    // The mapping restores stored entries through this constructor; its parameter names match the properties.
    private OpeningBalance(
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
        Money amount)
        : base(id, ownerId, schemaVersion, recordedAt, occurredOn, note, tags, groupId, source, externalId, bucketId, amount)
    {
    }

    /// <summary>
    /// A new opening balance. Fails like <see cref="Deposit.Create"/>. Whether the bucket already has
    /// one is checked by the caller against the state.
    /// </summary>
    /// <param name="id">The client-generated ID.</param>
    /// <param name="ownerId">The owner.</param>
    /// <param name="bucketId">The bucket.</param>
    /// <param name="amount">The amount.</param>
    /// <param name="occurredOn">The business date.</param>
    /// <param name="today">Today in the owner's time zone.</param>
    /// <param name="info">Note, tags and source.</param>
    /// <param name="recordedAt">Now, from the clock.</param>
    public static Result<OpeningBalance> Create(
        JournalEntryId id,
        OwnerId ownerId,
        BucketId bucketId,
        Money amount,
        DateOnly occurredOn,
        DateOnly today,
        EntryInfo info,
        DateTimeOffset recordedAt)
    {
        var check = Check(amount, occurredOn, today, info);
        return check.IsFailed
            ? check.ToResult<OpeningBalance>()
            : Result.Ok(new OpeningBalance(id.Value, ownerId, CurrentSchemaVersion, recordedAt, occurredOn, info.Note, info.Tags, info.GroupId, info.Source, externalId: null, bucketId, amount));
    }
}
