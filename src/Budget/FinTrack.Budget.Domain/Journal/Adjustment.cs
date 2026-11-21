using FinTrack.Budget.Domain.Buckets;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FluentResults;

namespace FinTrack.Budget.Domain.Journal;

/// <summary>
/// A manual correction of one bucket with a mandatory reason (ARCHITECTURE 4.2, type name <c>Adjustment</c>,
/// plan 1b B14). The one single-bucket entry whose type does not give the direction: <see cref="Direction"/>
/// does, and the amount stays above zero (D13). An increase raises <c>NetContributed</c>, a decrease lowers it;
/// never income (invariant 5), tracked on its own (<c>AdjustedIn</c>, <c>AdjustedOut</c>), exempt from the
/// limit (B12). A decrease is held to invariant 1 like a spend (<see cref="BudgetErrorCodes.BucketInsufficientFunds"/>);
/// allowed on any active bucket, <c>Unallocated</c> and investment buckets included.
/// </summary>
/// <remarks>The reason is the common note, required for this type: the validator reports a missing one
/// (<c>validation.failed</c>, field <c>note</c>), so one that reaches the factory without it is a bug.</remarks>
public sealed class Adjustment : SingleBucketEntry
{
    /// <summary>The stored shape's version.</summary>
    public const int CurrentSchemaVersion = 1;

    // The mapping restores stored entries through this constructor; its parameter names match the properties.
    private Adjustment(
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
        AdjustmentDirection direction)
        : base(id, ownerId, schemaVersion, recordedAt, occurredOn, note, tags, groupId, source, externalId, bucketId, amount)
    {
        Direction = direction;
    }

    /// <summary>Whether the adjustment raises or lowers the bucket's <c>NetContributed</c>.</summary>
    public AdjustmentDirection Direction { get; }

    /// <summary>
    /// A new adjustment. Fails like <see cref="Deposit.Create"/>; whether the bucket exists, is active and has
    /// the amount's currency, and whether a decrease keeps it at or above zero, is checked against the state.
    /// </summary>
    /// <param name="id">The client-generated ID.</param>
    /// <param name="ownerId">The owner.</param>
    /// <param name="bucketId">The adjusted bucket.</param>
    /// <param name="amount">The amount.</param>
    /// <param name="direction">Up or down.</param>
    /// <param name="occurredOn">The business date.</param>
    /// <param name="today">Today in the owner's time zone.</param>
    /// <param name="info">Note (required: the reason), tags and source.</param>
    /// <param name="recordedAt">Now, from the clock.</param>
    /// <exception cref="ArgumentException">The note is missing (a bug: the validator requires it).</exception>
    /// <exception cref="ArgumentOutOfRangeException">The direction is not defined (a bug).</exception>
    public static Result<Adjustment> Create(
        JournalEntryId id,
        OwnerId ownerId,
        BucketId bucketId,
        Money amount,
        AdjustmentDirection direction,
        DateOnly occurredOn,
        DateOnly today,
        EntryInfo info,
        DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.Note is null)
        {
            throw new ArgumentException("An adjustment needs a note: the reason for the correction.", nameof(info));
        }

        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown adjustment direction.");
        }

        var check = Check(amount, occurredOn, today, info);
        return check.IsFailed
            ? check.ToResult<Adjustment>()
            : Result.Ok(new Adjustment(id.Value, ownerId, CurrentSchemaVersion, recordedAt, occurredOn, info.Note, info.Tags, info.GroupId, info.Source, externalId: null, bucketId, amount, direction));
    }

    /// <inheritdoc />
    public override bool HasSameContent(JournalEntry other) =>
        base.HasSameContent(other)
        && other is Adjustment adjustment
        && Direction == adjustment.Direction;
}
