using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Shared.Kernel.Time;
using FluentResults;
using FluentValidation;
using Mediator;

namespace FinTrack.Budget.Application.Plans;

/// <summary>
/// A new contribution plan for one bucket (<c>POST /api/v1/plans</c>, ARCHITECTURE 4.5, plan 1b B7). Writes a
/// <c>PlanCreated</c>, whose ID is the plan's. A plan may start in the past (it has no periods yet to change, so invariant 6
/// holds) or later. Fails with <see cref="BudgetErrorCodes.BucketNotFound"/> (404); <see cref="BudgetErrorCodes.BucketSystemBucket"/>,
/// <see cref="BudgetErrorCodes.BucketArchived"/>, <see cref="BudgetErrorCodes.BucketCurrencyMismatch"/> (422); the entry's own rules.
/// Repeats as for <see cref="AddDeposit"/>.
/// </summary>
/// <param name="Id">The entry's ID, a version 7 UUID from the client; also the plan's ID.</param>
/// <param name="BucketId">The bucket.</param>
/// <param name="Amount">The monthly amount, strictly positive, in the bucket's currency.</param>
/// <param name="ValidFrom">The first month.</param>
/// <param name="ValidTo">The last month, or <c>null</c>.</param>
/// <param name="OccurredOn">The day the owner made the plan.</param>
/// <param name="Note">An optional note.</param>
/// <param name="Tags">Tags, possibly none.</param>
public sealed record CreatePlan(
    Guid Id,
    Guid BucketId,
    Money Amount,
    YearMonth ValidFrom,
    YearMonth? ValidTo,
    DateOnly OccurredOn,
    string? Note,
    IReadOnlyList<string> Tags) : ICommand<Result>, ILedgerCommand;

/// <summary>Shape checks: a bucket ID, the amount, note and tags.</summary>
public sealed class CreatePlanValidator : AbstractValidator<CreatePlan>
{
    /// <summary>Creates the rules.</summary>
    public CreatePlanValidator()
    {
        RuleFor(command => command.BucketId).NotEmpty();
        RuleFor(command => command.Amount).NotNull();
        RuleFor(command => command.Note).EntryNote();
        RuleFor(command => command.Tags).EntryTags();
        RuleForEach(command => command.Tags).EntryTag();
    }
}

/// <summary>Finds the bucket, checks it, and writes the plan through <see cref="JournalWriter"/>.</summary>
public sealed class CreatePlanHandler(JournalWriter writer) : ICommandHandler<CreatePlan, Result>
{
    /// <inheritdoc />
    public ValueTask<Result> Handle(CreatePlan command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return writer.WriteAsync(
            command.Id,
            context => context.ResolveTarget(command.BucketId, unallocated: null).Map(bucket => new JournalDraft(
                PlanCreated.Create(
                        context.EntryId, context.Owner, bucket.BucketId, command.Amount, command.ValidFrom, command.ValidTo, command.OccurredOn,
                        context.Today, new EntryInfo(command.Note, command.Tags, EntrySource.Manual), context.Now)
                    .Map(entry => (JournalEntry)entry),
                Rules(bucket, command.Amount))),
            cancellationToken);
    }

    // A user's bucket (plans on Unallocated are rejected, B7), active and in the amount's currency.
    private static Result Rules(Bucket bucket, Money amount) =>
        bucket.SystemRole != BucketSystemRole.None
            ? Result.Fail(AppError.BusinessRule(BudgetErrorCodes.BucketSystemBucket))
            : JournalWriteContext.CheckBucket(bucket, amount);
}
