using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FluentResults;
using FluentValidation;
using Mediator;

namespace FinTrack.Budget.Application.Journal;

/// <summary>
/// Money borrowed from a savings bucket with the obligation to return it (<c>POST /api/v1/journal/borrows</c>, plan 1b
/// B2): opens a loan whose ID is this entry's ID. The destination is a bucket, the <c>Unallocated</c> bucket of the
/// lender's currency (created in the same commit when missing), or neither (the money left the app). Fails with
/// <see cref="BudgetErrorCodes.BucketNotFound"/> (404); <see cref="BudgetErrorCodes.BucketSystemBucket"/> (the lender is
/// <c>Unallocated</c>), <see cref="BudgetErrorCodes.BucketBorrowNotAllowed"/> (an investment lender),
/// <see cref="BudgetErrorCodes.BucketArchived"/>, <see cref="BudgetErrorCodes.BucketCurrencyMismatch"/> and
/// <see cref="BudgetErrorCodes.BucketInsufficientFunds"/> (the lender below zero on some day; 422 each);
/// <see cref="BudgetErrorCodes.BorrowSameBucket"/> (400). Repeats as for <see cref="AddDeposit"/>.
/// </summary>
/// <param name="Id">The entry's ID, a version 7 UUID from the client; also the loan's ID.</param>
/// <param name="LenderBucketId">The savings bucket the money is borrowed from.</param>
/// <param name="Principal">Strictly positive, in the lender's currency.</param>
/// <param name="OccurredOn">The business date.</param>
/// <param name="ToBucketId">The destination bucket, or <c>null</c>.</param>
/// <param name="ToUnallocated">The currency of the destination <c>Unallocated</c> bucket, or <c>null</c>; with neither, the money left the app.</param>
/// <param name="Note">An optional note.</param>
/// <param name="Tags">Tags, possibly none.</param>
public sealed record AddBorrow(
    Guid Id,
    Guid LenderBucketId,
    Money Principal,
    DateOnly OccurredOn,
    Guid? ToBucketId,
    Currency? ToUnallocated,
    string? Note,
    IReadOnlyList<string> Tags) : ICommand<Result>, ILedgerCommand;

/// <summary>Shape checks: the principal, at most one destination, note and tags.</summary>
public sealed class AddBorrowValidator : AbstractValidator<AddBorrow>
{
    /// <summary>Creates the rules.</summary>
    public AddBorrowValidator()
    {
        RuleFor(command => command.Principal).NotNull();
        RuleFor(command => command.ToBucketId)
            .Must((command, bucketId) => bucketId is null || command.ToUnallocated is null)
            .WithErrorCode(EntryRules.AtMostOneTarget);
        RuleFor(command => command.Note).EntryNote();
        RuleFor(command => command.Tags).EntryTags();
        RuleForEach(command => command.Tags).EntryTag();
    }
}

/// <summary>Resolves the lender and the destination, checks both, and writes the borrow through <see cref="JournalWriter"/>.</summary>
public sealed class AddBorrowHandler(JournalWriter writer) : ICommandHandler<AddBorrow, Result>
{
    /// <inheritdoc />
    public ValueTask<Result> Handle(AddBorrow command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return writer.WriteAsync(
            command.Id,
            context =>
            {
                var lender = context.ResolveTarget(command.LenderBucketId, unallocated: null);
                if (lender.IsFailed)
                {
                    return lender.ToResult<JournalDraft>();
                }

                Bucket? destination = null;
                if (command.ToBucketId is not null || command.ToUnallocated is not null)
                {
                    var resolved = context.ResolveTarget(command.ToBucketId, command.ToUnallocated);
                    if (resolved.IsFailed)
                    {
                        return resolved.ToResult<JournalDraft>();
                    }

                    destination = resolved.Value;
                }

                var entry = Borrow.Create(
                        context.EntryId, context.Owner, lender.Value.BucketId, command.Principal, destination?.BucketId, command.OccurredOn, context.Today,
                        new EntryInfo(command.Note, command.Tags, EntrySource.Manual), context.Now)
                    .Map(borrow => (JournalEntry)borrow);

                var rules = LenderRule(lender.Value)
                    .Bind(() => JournalWriteContext.CheckBucket(lender.Value, command.Principal))
                    .Bind(() => destination is null ? Result.Ok() : JournalWriteContext.CheckBucket(destination, command.Principal));

                return Result.Ok(new JournalDraft(entry, rules));
            },
            cancellationToken);
    }

    // Loans come from a user's savings bucket only (B2, 4.9): Unallocated holds money not yet distributed and lends nothing.
    private static Result LenderRule(Bucket lender)
    {
        if (lender.SystemRole != BucketSystemRole.None)
        {
            return Result.Fail(AppError.BusinessRule(BudgetErrorCodes.BucketSystemBucket));
        }

        return lender.Kind == BucketKind.Investment
            ? Result.Fail(AppError.BusinessRule(BudgetErrorCodes.BucketBorrowNotAllowed))
            : Result.Ok();
    }
}
