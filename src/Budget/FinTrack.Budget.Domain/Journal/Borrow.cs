using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Loans;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FluentResults;

namespace FinTrack.Budget.Domain.Journal;

/// <summary>
/// Money taken from a savings bucket with the obligation to return it (ARCHITECTURE 4.2, type name <c>Borrow</c>, plan
/// 1b B2). Opens a loan whose ID is this entry's ID (<see cref="LoanId"/>). Raises the lender's
/// <c>OutstandingPrincipal</c> (its <c>NetContributed</c> is unchanged, so <c>Available</c> falls) and, when the money
/// stays in the app, the destination's <c>NetContributed</c>, like a transfer in (B3). Never income.
/// </summary>
/// <remarks>
/// Whether the lender is an active savings bucket of the user, and the destination an active bucket of the lender's
/// currency, is checked by the caller against the state; the lender's invariant 1 by the replay.
/// </remarks>
public sealed class Borrow : JournalEntry
{
    /// <summary>The stored shape's version.</summary>
    public const int CurrentSchemaVersion = 1;

    // The mapping restores stored entries through this constructor; its parameter names match the properties.
    private Borrow(
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
        BucketId lenderBucketId,
        Money principal,
        BucketId? toBucketId)
        : base(id, ownerId, schemaVersion, recordedAt, occurredOn, note, tags, groupId, source, externalId)
    {
        LenderBucketId = lenderBucketId;
        Principal = principal;
        ToBucketId = toBucketId;
    }

    /// <summary>The loan this entry opens.</summary>
    public LoanId LoanId => LoanId.From(Id);

    /// <summary>The savings bucket the money is borrowed from.</summary>
    public BucketId LenderBucketId { get; }

    /// <summary>What is borrowed, above zero, in the lender's currency.</summary>
    public Money Principal { get; }

    /// <summary>The bucket the money goes to, or <c>null</c> when it leaves the app.</summary>
    public BucketId? ToBucketId { get; }

    /// <summary>
    /// A new borrow. Fails, reporting every broken rule, with <see cref="ErrorCodes.MoneyAmountNotPositive"/>,
    /// <see cref="BudgetErrorCodes.BorrowSameBucket"/> (both 400) or <see cref="BudgetErrorCodes.EntryDateInFuture"/> (422).
    /// </summary>
    /// <param name="id">The client-generated ID; also the loan's ID.</param>
    /// <param name="ownerId">The owner.</param>
    /// <param name="lenderBucketId">The lender.</param>
    /// <param name="principal">What is borrowed.</param>
    /// <param name="toBucketId">The destination, or <c>null</c> for outside the app.</param>
    /// <param name="occurredOn">The business date.</param>
    /// <param name="today">Today in the owner's time zone.</param>
    /// <param name="info">Note, tags and source.</param>
    /// <param name="recordedAt">Now, from the clock.</param>
    public static Result<Borrow> Create(
        JournalEntryId id,
        OwnerId ownerId,
        BucketId lenderBucketId,
        Money principal,
        BucketId? toBucketId,
        DateOnly occurredOn,
        DateOnly today,
        EntryInfo info,
        DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(info);

        var errors = new List<IError>();
        if (principal.Amount <= 0m)
        {
            errors.Add(AppError.Validation(ErrorCodes.MoneyAmountNotPositive));
        }

        if (toBucketId == lenderBucketId)
        {
            errors.Add(AppError.Validation(BudgetErrorCodes.BorrowSameBucket));
        }

        if (occurredOn > today)
        {
            errors.Add(AppError.BusinessRule(BudgetErrorCodes.EntryDateInFuture));
        }

        return errors.Count > 0
            ? Result.Fail<Borrow>(errors)
            : Result.Ok(new Borrow(id.Value, ownerId, CurrentSchemaVersion, recordedAt, occurredOn, info.Note, info.Tags, info.GroupId, info.Source, externalId: null,
                lenderBucketId, principal, toBucketId));
    }

    /// <inheritdoc />
    public override bool HasSameContent(JournalEntry other) =>
        HasSameCommonContent(other)
        && other is Borrow borrow
        && LenderBucketId == borrow.LenderBucketId
        && Principal == borrow.Principal
        && ToBucketId == borrow.ToBucketId;
}
