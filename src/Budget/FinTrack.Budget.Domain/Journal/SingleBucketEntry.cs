using FinTrack.Budget.Domain.Buckets;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FluentResults;

namespace FinTrack.Budget.Domain.Journal;

/// <summary>
/// An entry that moves one strictly positive amount into or out of one bucket: <see cref="OpeningBalance"/>,
/// <see cref="Deposit"/> and <see cref="Spend"/>. The type decides the direction (ARCHITECTURE 4.2).
/// </summary>
public abstract class SingleBucketEntry : JournalEntry
{
    private protected SingleBucketEntry(
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
        : base(id, ownerId, schemaVersion, recordedAt, occurredOn, note, tags, groupId, source, externalId)
    {
        BucketId = bucketId;
        Amount = amount;
    }

    /// <summary>The bucket.</summary>
    public BucketId BucketId { get; }

    /// <summary>The amount, strictly positive, in the bucket's currency.</summary>
    public Money Amount { get; }

    /// <inheritdoc />
    public override bool HasSameContent(JournalEntry other) =>
        HasSameCommonContent(other)
        && other is SingleBucketEntry entry
        && BucketId == entry.BucketId
        && Amount == entry.Amount;

    /// <summary>
    /// The rules every single-bucket entry shares: the amount is above zero
    /// (<see cref="ErrorCodes.MoneyAmountNotPositive"/>, D13) and the date is not after
    /// <paramref name="today"/> (<see cref="BudgetErrorCodes.EntryDateInFuture"/>, D12). Both are reported.
    /// </summary>
    private protected static Result Check(Money amount, DateOnly occurredOn, DateOnly today, EntryInfo info)
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

        return errors.Count > 0 ? Result.Fail(errors) : Result.Ok();
    }
}
