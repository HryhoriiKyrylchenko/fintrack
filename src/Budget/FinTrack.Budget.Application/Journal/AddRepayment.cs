using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.Loans;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FluentResults;
using FluentValidation;
using Mediator;

namespace FinTrack.Budget.Application.Journal;

/// <summary>
/// Loan principal paid back to its lender (<c>POST /api/v1/journal/repayments</c>, plan 1b B2, B4). The source is a
/// bucket, the <c>Unallocated</c> bucket of the lender's currency, or neither (new money from outside). Never income.
/// Fails with <see cref="BudgetErrorCodes.LoanNotFound"/> or <see cref="BudgetErrorCodes.BucketNotFound"/> (404);
/// <see cref="BudgetErrorCodes.LoanClosed"/>, <see cref="BudgetErrorCodes.RepaymentFromLender"/>,
/// <see cref="BudgetErrorCodes.BucketArchived"/>, <see cref="BudgetErrorCodes.BucketCurrencyMismatch"/>,
/// <see cref="BudgetErrorCodes.LoanRepaymentExceedsOutstanding"/> (above the outstanding principal on some day, also
/// before the borrow) and <see cref="BudgetErrorCodes.BucketInsufficientFunds"/> (the source below zero; 422 each).
/// Repeats as for <see cref="AddDeposit"/>.
/// </summary>
/// <param name="Id">The entry's ID, a version 7 UUID from the client.</param>
/// <param name="LoanId">The loan: the ID of its borrow.</param>
/// <param name="Amount">Strictly positive, in the lender's currency.</param>
/// <param name="OccurredOn">The business date.</param>
/// <param name="FromBucketId">The source bucket, or <c>null</c>.</param>
/// <param name="FromUnallocated">The currency of the source <c>Unallocated</c> bucket, or <c>null</c>; with neither, new money from outside.</param>
/// <param name="Note">An optional note.</param>
/// <param name="Tags">Tags, possibly none.</param>
public sealed record AddRepayment(
    Guid Id,
    Guid LoanId,
    Money Amount,
    DateOnly OccurredOn,
    Guid? FromBucketId,
    Currency? FromUnallocated,
    string? Note,
    IReadOnlyList<string> Tags) : ICommand<Result>, ILedgerCommand;

/// <summary>Shape checks: a loan ID, the amount, at most one source, note and tags.</summary>
public sealed class AddRepaymentValidator : AbstractValidator<AddRepayment>
{
    /// <summary>Creates the rules.</summary>
    public AddRepaymentValidator()
    {
        RuleFor(command => command.LoanId).NotEmpty();
        RuleFor(command => command.Amount).NotNull();
        RuleFor(command => command.FromBucketId)
            .Must((command, bucketId) => bucketId is null || command.FromUnallocated is null)
            .WithErrorCode(EntryRules.AtMostOneTarget);
        RuleFor(command => command.Note).EntryNote();
        RuleFor(command => command.Tags).EntryTags();
        RuleForEach(command => command.Tags).EntryTag();
    }
}

/// <summary>
/// Resolves the source, finds the loan (open today), checks both, and writes the repayment through
/// <see cref="JournalWriter"/>, whose replay holds it to invariant 4.
/// </summary>
public sealed class AddRepaymentHandler(JournalWriter writer) : ICommandHandler<AddRepayment, Result>
{
    /// <inheritdoc />
    public ValueTask<Result> Handle(AddRepayment command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return writer.WriteAsync(
            command.Id,
            context =>
            {
                Bucket? source = null;
                if (command.FromBucketId is not null || command.FromUnallocated is not null)
                {
                    var resolved = context.ResolveTarget(command.FromBucketId, command.FromUnallocated);
                    if (resolved.IsFailed)
                    {
                        return resolved.ToResult<JournalDraft>();
                    }

                    source = resolved.Value;
                }

                // The entry is built from the command's loan ID and the loan checked among the rules, after a repeat: a
                // repayment repeated after it and its borrow were reversed must still be recognised (invariant 13).
                var loanId = Domain.Loans.LoanId.From(command.LoanId);
                var entry = Repayment.Create(
                        context.EntryId, context.Owner, loanId, command.Amount, source?.BucketId, command.OccurredOn, context.Today,
                        new EntryInfo(command.Note, command.Tags, EntrySource.Manual), context.Now)
                    .Map(repayment => (JournalEntry)repayment);

                return Result.Ok(new JournalDraft(entry, Rules(context.Balances.FindLoan(loanId), source, command.Amount)));
            },
            cancellationToken);
    }

    // Checked after a repeat (the loan a repeated repayment closed is closed now, and it may be reversed since): a loan (an
    // effective borrow: an unknown ID, another entry's ID or a reversed borrow is not one), open, the lender's currency, a
    // source other than the lender, active and in the lender's currency.
    private static Result Rules(Loan? loan, Bucket? source, Money amount)
    {
        if (loan is null)
        {
            return Result.Fail(AppError.NotFound(BudgetErrorCodes.LoanNotFound));
        }

        if (loan.Status == LoanStatus.Closed)
        {
            return Result.Fail(AppError.BusinessRule(BudgetErrorCodes.LoanClosed));
        }

        if (amount.Currency != loan.Principal.Currency)
        {
            return Result.Fail(AppError.BusinessRule(BudgetErrorCodes.BucketCurrencyMismatch));
        }

        if (source is null)
        {
            return Result.Ok();
        }

        return source.BucketId == loan.LenderBucketId
            ? Result.Fail(AppError.BusinessRule(BudgetErrorCodes.RepaymentFromLender))
            : JournalWriteContext.CheckBucket(source, amount);
    }
}
