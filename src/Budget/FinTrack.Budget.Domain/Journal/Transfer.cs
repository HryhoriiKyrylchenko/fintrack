using FinTrack.Budget.Domain.Buckets;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FluentResults;

namespace FinTrack.Budget.Domain.Journal;

/// <summary>
/// Money moved between two different buckets, as one entry for both sides (ARCHITECTURE 4.2, type name
/// <c>Transfer</c>, plan 1a D6). Never income.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FromAmount"/> is everything that leaves the source, in its currency, fee included;
/// <see cref="ToAmount"/> is what arrives, in the destination's currency; <see cref="Fee"/> is optional, in
/// the source currency, and part of <see cref="FromAmount"/>. The amounts are the truth: <see cref="Rate"/> is
/// derived from them (<c>ToAmount ÷ (FromAmount − Fee)</c>, banker's rounding to
/// <see cref="RateDecimalPlaces"/> places) and stored for display only. In one currency
/// <c>ToAmount = FromAmount − Fee</c> exactly.
/// </para>
/// <para>
/// Balances: the source loses <see cref="FromAmount"/>, the destination gains <see cref="ToAmount"/>. Whether
/// both buckets exist, are active and hold the amounts' currencies is checked by the caller against the state.
/// </para>
/// </remarks>
public sealed class Transfer : JournalEntry
{
    /// <summary>The stored shape's version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The precision of the stored rate.</summary>
    public const int RateDecimalPlaces = 12;

    // The mapping restores stored entries through this constructor; its parameter names match the properties.
    private Transfer(
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
        BucketId fromBucketId,
        BucketId toBucketId,
        Money fromAmount,
        Money toAmount,
        Money? fee,
        decimal rate)
        : base(id, ownerId, schemaVersion, recordedAt, occurredOn, note, tags, groupId, source, externalId)
    {
        FromBucketId = fromBucketId;
        ToBucketId = toBucketId;
        FromAmount = fromAmount;
        ToAmount = toAmount;
        Fee = fee;
        Rate = rate;
    }

    /// <summary>The bucket the money leaves.</summary>
    public BucketId FromBucketId { get; }

    /// <summary>The bucket the money arrives in.</summary>
    public BucketId ToBucketId { get; }

    /// <summary>Everything that leaves the source, fee included, in the source's currency.</summary>
    public Money FromAmount { get; }

    /// <summary>What arrives, in the destination's currency.</summary>
    public Money ToAmount { get; }

    /// <summary>The fee, part of <see cref="FromAmount"/>, in the source's currency; <c>null</c> when there was none.</summary>
    public Money? Fee { get; }

    /// <summary>Destination units per source unit, derived from the amounts; for display.</summary>
    public decimal Rate { get; }

    /// <summary>
    /// A new transfer. Fails, reporting every broken rule, with <see cref="ErrorCodes.MoneyAmountNotPositive"/>,
    /// <see cref="BudgetErrorCodes.BucketCurrencyMismatch"/> (a fee not in the source currency),
    /// <see cref="BudgetErrorCodes.TransferSameBucket"/>, <see cref="BudgetErrorCodes.TransferFeeExceedsAmount"/>,
    /// <see cref="BudgetErrorCodes.TransferAmountsInconsistent"/> (all 400) or
    /// <see cref="BudgetErrorCodes.EntryDateInFuture"/> (422).
    /// </summary>
    /// <param name="id">The client-generated ID.</param>
    /// <param name="ownerId">The owner.</param>
    /// <param name="fromBucketId">The source.</param>
    /// <param name="toBucketId">The destination.</param>
    /// <param name="fromAmount">Everything that leaves the source, fee included.</param>
    /// <param name="toAmount">What arrives.</param>
    /// <param name="fee">The fee, or <c>null</c>.</param>
    /// <param name="occurredOn">The business date.</param>
    /// <param name="today">Today in the owner's time zone.</param>
    /// <param name="info">Note, tags and source.</param>
    /// <param name="recordedAt">Now, from the clock.</param>
    public static Result<Transfer> Create(
        JournalEntryId id,
        OwnerId ownerId,
        BucketId fromBucketId,
        BucketId toBucketId,
        Money fromAmount,
        Money toAmount,
        Money? fee,
        DateOnly occurredOn,
        DateOnly today,
        EntryInfo info,
        DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(fromAmount);
        ArgumentNullException.ThrowIfNull(toAmount);
        ArgumentNullException.ThrowIfNull(info);

        var errors = new List<IError>();

        if (fromAmount.Amount <= 0m || toAmount.Amount <= 0m || fee is { Amount: <= 0m })
        {
            errors.Add(AppError.Validation(ErrorCodes.MoneyAmountNotPositive));
        }

        if (fromBucketId == toBucketId)
        {
            errors.Add(AppError.Validation(BudgetErrorCodes.TransferSameBucket));
        }

        var feeInSourceCurrency = fee is null || fee.Currency == fromAmount.Currency;
        if (!feeInSourceCurrency)
        {
            errors.Add(AppError.Validation(BudgetErrorCodes.BucketCurrencyMismatch));
        }
        else if (fee is not null && fee >= fromAmount)
        {
            errors.Add(AppError.Validation(BudgetErrorCodes.TransferFeeExceedsAmount));
        }
        else if (fromAmount.Currency == toAmount.Currency && toAmount != fromAmount - (fee ?? Money.Zero(fromAmount.Currency)))
        {
            errors.Add(AppError.Validation(BudgetErrorCodes.TransferAmountsInconsistent));
        }

        if (occurredOn > today)
        {
            errors.Add(AppError.BusinessRule(BudgetErrorCodes.EntryDateInFuture));
        }

        if (errors.Count > 0)
        {
            return Result.Fail<Transfer>(errors);
        }

        var sent = fromAmount - (fee ?? Money.Zero(fromAmount.Currency));
        var rate = decimal.Round(toAmount.Amount / sent.Amount, RateDecimalPlaces, MidpointRounding.ToEven);

        return Result.Ok(new Transfer(
            id.Value, ownerId, CurrentSchemaVersion, recordedAt, occurredOn, info.Note, info.Tags, groupId: null, info.Source, externalId: null,
            fromBucketId, toBucketId, fromAmount, toAmount, fee, rate));
    }

    /// <inheritdoc />
    public override bool HasSameContent(JournalEntry other) =>
        HasSameCommonContent(other)
        && other is Transfer transfer
        && FromBucketId == transfer.FromBucketId
        && ToBucketId == transfer.ToBucketId
        && FromAmount == transfer.FromAmount
        && ToAmount == transfer.ToAmount
        && Fee == transfer.Fee;
}
