using FinTrack.Budget.Application.Plans;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Monetary;
using FluentResults;
using FluentValidation;
using Mediator;

namespace FinTrack.Budget.Application.Journal;

/// <summary>
/// Money moved between two different buckets (<c>POST /api/v1/journal/transfers</c>, plan 1a D6). Each
/// side is a bucket by ID or the <c>Unallocated</c> bucket of a currency (created in the same commit when
/// missing). The amounts are the truth; the rate is derived. A savings source never goes below zero at
/// any point (D2); investment buckets may send and receive and may go below zero (4.9). Repeats as for
/// <see cref="AddDeposit"/>.
/// </summary>
/// <param name="Id">The entry's ID, a version 7 UUID from the client.</param>
/// <param name="FromAmount">Everything that leaves the source, fee included, in the source's currency.</param>
/// <param name="ToAmount">What arrives, in the destination's currency.</param>
/// <param name="Fee">The fee, part of <paramref name="FromAmount"/>, or <c>null</c>.</param>
/// <param name="OccurredOn">The business date.</param>
/// <param name="FromBucketId">The source bucket, or <c>null</c> when <paramref name="FromUnallocated"/> is set.</param>
/// <param name="FromUnallocated">The currency of the source <c>Unallocated</c> bucket, or <c>null</c>.</param>
/// <param name="ToBucketId">The destination bucket, or <c>null</c> when <paramref name="ToUnallocated"/> is set.</param>
/// <param name="ToUnallocated">The currency of the destination <c>Unallocated</c> bucket, or <c>null</c>.</param>
/// <param name="Note">An optional note.</param>
/// <param name="Tags">Tags, possibly none.</param>
/// <param name="PlanId">
/// The destination's contribution plan that the transfer pays with <paramref name="ToAmount"/> (plan 1b B9), or <c>null</c>; fails with
/// <see cref="BudgetErrorCodes.PlanNotFound"/> (404) or <see cref="BudgetErrorCodes.PlanBucketMismatch"/> (422). Still never income.
/// </param>
public sealed record AddTransfer(
    Guid Id,
    Money FromAmount,
    Money ToAmount,
    Money? Fee,
    DateOnly OccurredOn,
    Guid? FromBucketId,
    Currency? FromUnallocated,
    Guid? ToBucketId,
    Currency? ToUnallocated,
    string? Note,
    IReadOnlyList<string> Tags,
    Guid? PlanId = null) : ICommand<Result>, ILedgerCommand;

/// <summary>Shape checks: each side names exactly one target; note and tags.</summary>
public sealed class AddTransferValidator : AbstractValidator<AddTransfer>
{
    /// <summary>Creates the rules.</summary>
    public AddTransferValidator()
    {
        RuleFor(command => command.FromAmount).NotNull();
        RuleFor(command => command.ToAmount).NotNull();
        RuleFor(command => command.FromBucketId)
            .Must((command, bucketId) => (bucketId is null) != (command.FromUnallocated is null))
            .WithErrorCode(BucketEntryValidator<AddDeposit>.ExactlyOneTarget);
        RuleFor(command => command.ToBucketId)
            .Must((command, bucketId) => (bucketId is null) != (command.ToUnallocated is null))
            .WithErrorCode(BucketEntryValidator<AddDeposit>.ExactlyOneTarget);
        RuleFor(command => command.PlanId).NotEqual(Guid.Empty);
        RuleFor(command => command.Note).EntryNote();
        RuleFor(command => command.Tags).EntryTags();
        RuleForEach(command => command.Tags).EntryTag();
    }
}

/// <summary>Resolves both sides, checks both buckets, and writes the transfer through <see cref="JournalWriter"/>.</summary>
public sealed class AddTransferHandler(JournalWriter writer) : ICommandHandler<AddTransfer, Result>
{
    /// <inheritdoc />
    public ValueTask<Result> Handle(AddTransfer command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return writer.WriteAsync(
            command.Id,
            context =>
            {
                var from = context.ResolveTarget(command.FromBucketId, command.FromUnallocated);
                if (from.IsFailed)
                {
                    return from.ToResult<JournalDraft>();
                }

                var to = context.ResolveTarget(command.ToBucketId, command.ToUnallocated);
                if (to.IsFailed)
                {
                    return to.ToResult<JournalDraft>();
                }

                var entry = Transfer.Create(
                        context.EntryId, context.Owner, from.Value.BucketId, to.Value.BucketId, command.FromAmount, command.ToAmount, command.Fee,
                        command.OccurredOn, context.Today, new EntryInfo(command.Note, command.Tags, EntrySource.Manual), context.Now,
                        command.PlanId is { } planId ? Domain.Plans.PlanId.From(planId) : null)
                    .Map(transfer => (JournalEntry)transfer);

                var rules = JournalWriteContext.CheckBucket(from.Value, command.FromAmount)
                    .Bind(() => JournalWriteContext.CheckBucket(to.Value, command.ToAmount))
                    .Bind(() => PlanRules.CheckContribution(command.PlanId, to.Value, context.Balances));

                return Result.Ok(new JournalDraft(entry, rules));
            },
            cancellationToken);
    }
}
