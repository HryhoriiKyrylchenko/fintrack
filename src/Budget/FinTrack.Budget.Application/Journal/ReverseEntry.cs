using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Results;
using FluentResults;
using FluentValidation;
using Mediator;

namespace FinTrack.Budget.Application.Journal;

/// <summary>
/// Cancels one earlier entry (<c>POST /api/v1/journal/reversals</c>, plan 1a D3): the entry then counts as
/// if it never happened, at its original date. Fails with <see cref="BudgetErrorCodes.EntryNotFound"/> (404),
/// <see cref="BudgetErrorCodes.EntryNotReversible"/> (a reversal), <see cref="BudgetErrorCodes.EntryAlreadyReversed"/>,
/// <see cref="BudgetErrorCodes.BucketArchived"/> (it touches an archived bucket) or
/// <see cref="BudgetErrorCodes.BucketInsufficientFunds"/> (the journal without it would be below zero on
/// some day; 422 each). Repeats as for <see cref="AddDeposit"/>.
/// </summary>
/// <param name="Id">The reversal's ID, a version 7 UUID from the client.</param>
/// <param name="ReversedEntryId">The entry to cancel.</param>
/// <param name="OccurredOn">The day of the correction.</param>
/// <param name="Note">An optional note, for example why.</param>
/// <param name="Tags">Tags, possibly none.</param>
public sealed record ReverseEntry(Guid Id, Guid ReversedEntryId, DateOnly OccurredOn, string? Note, IReadOnlyList<string> Tags)
    : ICommand<Result>, ILedgerCommand;

/// <summary>Shape checks: note and tags.</summary>
public sealed class ReverseEntryValidator : AbstractValidator<ReverseEntry>
{
    /// <summary>Creates the rules.</summary>
    public ReverseEntryValidator()
    {
        RuleFor(command => command.Note).EntryNote();
        RuleFor(command => command.Tags).EntryTags();
        RuleForEach(command => command.Tags).EntryTag();
    }
}

/// <summary>Finds the entry, checks that it may be reversed, and writes the reversal through <see cref="JournalWriter"/>.</summary>
public sealed class ReverseEntryHandler(JournalWriter writer) : ICommandHandler<ReverseEntry, Result>
{
    /// <inheritdoc />
    public ValueTask<Result> Handle(ReverseEntry command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return writer.WriteAsync(
            command.Id,
            context =>
            {
                if (context.State.FindEntry(command.ReversedEntryId) is not { } target)
                {
                    return Result.Fail<JournalDraft>(AppError.NotFound(BudgetErrorCodes.EntryNotFound));
                }

                var entry = Reversal.Create(
                        context.EntryId, context.Owner, target.EntryId, command.OccurredOn, context.Today,
                        new EntryInfo(command.Note, command.Tags, EntrySource.Manual), context.Now)
                    .Map(reversal => (JournalEntry)reversal);

                return Result.Ok(new JournalDraft(entry, Rules(target, context.State)));
            },
            cancellationToken);
    }

    private static Result Rules(JournalEntry target, BudgetSnapshot state)
    {
        if (target is Reversal)
        {
            return Result.Fail(AppError.BusinessRule(BudgetErrorCodes.EntryNotReversible));
        }

        if (BudgetState.ReversedEntryIds(state.Journal).Contains(target.Id))
        {
            return Result.Fail(AppError.BusinessRule(BudgetErrorCodes.EntryAlreadyReversed));
        }

        // An archived bucket accepts no change to its balance, and a reversal changes it.
        return BucketsOf(target).Any(bucketId => state.FindBucket(bucketId.Value) is { Status: BucketStatus.Archived })
            ? Result.Fail(AppError.BusinessRule(BudgetErrorCodes.BucketArchived))
            : Result.Ok();
    }

    private static IEnumerable<BucketId> BucketsOf(JournalEntry entry) => entry switch
    {
        SingleBucketEntry single => [single.BucketId],
        Transfer transfer => [transfer.FromBucketId, transfer.ToBucketId],
        _ => [],
    };
}
