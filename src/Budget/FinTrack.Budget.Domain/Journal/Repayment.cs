using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Loans;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FluentResults;

namespace FinTrack.Budget.Domain.Journal;

/// <summary>
/// Loan principal returned to its lender (ARCHITECTURE 4.2, type name <c>Repayment</c>, plan 1b B2). Lowers the lender's
/// <c>OutstandingPrincipal</c> and, when paid from a bucket, the source's <c>NetContributed</c>, like a transfer out (B3).
/// Never income (invariant 5) and never above the loan's outstanding principal at any point of the replay order
/// (invariant 4, <see cref="BudgetErrorCodes.LoanRepaymentExceedsOutstanding"/>).
/// </summary>
/// <remarks>
/// Whether the loan exists and is open, the source is an active bucket of the lender's currency other than the lender,
/// and the amount fits the loan, is checked by the caller against the state and by the replay.
/// </remarks>
public sealed class Repayment : JournalEntry
{
    /// <summary>The stored shape's version.</summary>
    public const int CurrentSchemaVersion = 1;

    // The mapping restores stored entries through this constructor; its parameter names match the properties.
    private Repayment(
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
        LoanId loanId,
        Money amount,
        BucketId? fromBucketId)
        : base(id, ownerId, schemaVersion, recordedAt, occurredOn, note, tags, groupId, source, externalId)
    {
        LoanId = loanId;
        Amount = amount;
        FromBucketId = fromBucketId;
    }

    /// <summary>The loan repaid.</summary>
    public LoanId LoanId { get; }

    /// <summary>The principal repaid, above zero, in the lender's currency.</summary>
    public Money Amount { get; }

    /// <summary>The bucket the money comes from, or <c>null</c> for new money from outside the app.</summary>
    public BucketId? FromBucketId { get; }

    /// <summary>
    /// A new repayment. Fails with <see cref="ErrorCodes.MoneyAmountNotPositive"/> (400) and/or
    /// <see cref="BudgetErrorCodes.EntryDateInFuture"/> (422).
    /// </summary>
    /// <param name="id">The client-generated ID.</param>
    /// <param name="ownerId">The owner.</param>
    /// <param name="loanId">The loan.</param>
    /// <param name="amount">The principal repaid.</param>
    /// <param name="fromBucketId">The source, or <c>null</c> for outside the app.</param>
    /// <param name="occurredOn">The business date.</param>
    /// <param name="today">Today in the owner's time zone.</param>
    /// <param name="info">Note, tags and source.</param>
    /// <param name="recordedAt">Now, from the clock.</param>
    public static Result<Repayment> Create(
        JournalEntryId id,
        OwnerId ownerId,
        LoanId loanId,
        Money amount,
        BucketId? fromBucketId,
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
            ? Result.Fail<Repayment>(errors)
            : Result.Ok(new Repayment(id.Value, ownerId, CurrentSchemaVersion, recordedAt, occurredOn, info.Note, info.Tags, info.GroupId, info.Source, externalId: null,
                loanId, amount, fromBucketId));
    }

    /// <inheritdoc />
    public override bool HasSameContent(JournalEntry other) =>
        HasSameCommonContent(other)
        && other is Repayment repayment
        && LoanId == repayment.LoanId
        && Amount == repayment.Amount
        && FromBucketId == repayment.FromBucketId;
}
