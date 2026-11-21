using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Monetary;
using FluentResults;
using FluentValidation;
using Mediator;
using Contract = FinTrack.Budget.Contracts.V1.Journal;

namespace FinTrack.Budget.Application.Journal;

/// <summary>
/// A manual correction of one bucket with a reason (<c>POST /api/v1/journal/adjustments</c>, plan 1b B14). An
/// increase raises <c>NetContributed</c>, a decrease lowers it; never income (invariant 5) and exempt from the limit
/// (B12). Allowed on any active bucket, <c>Unallocated</c> and investment buckets included, so it has no type rule
/// of its own; a decrease of a savings bucket is held to invariant 1 by the write path
/// (<see cref="BudgetErrorCodes.BucketInsufficientFunds"/> with the first day below zero). Target, repeats and the
/// other rules as for <see cref="AddDeposit"/>.
/// </summary>
/// <param name="Id">The entry's ID, a version 7 UUID from the client.</param>
/// <param name="Amount">Strictly positive, in the target's currency.</param>
/// <param name="Direction">Up or down.</param>
/// <param name="OccurredOn">The business date.</param>
/// <param name="BucketId">The bucket, or <c>null</c> when <paramref name="Unallocated"/> is set.</param>
/// <param name="Unallocated">The currency of the adjusted <c>Unallocated</c> bucket, or <c>null</c>.</param>
/// <param name="Note">Required: the reason.</param>
/// <param name="Tags">Tags, possibly none.</param>
public sealed record AddAdjustment(
    Guid Id,
    Money Amount,
    Contract.AdjustmentDirection Direction,
    DateOnly OccurredOn,
    Guid? BucketId,
    Currency? Unallocated,
    string? Note,
    IReadOnlyList<string> Tags) : ICommand<Result>, ILedgerCommand, IBucketEntryCommand;

/// <summary>
/// Shape checks (<see cref="BucketEntryValidator{TCommand}"/>), plus the two of an adjustment: a defined direction, and
/// the note, which is required here (a missing or empty note is <c>validation.failed</c> at <c>note</c> with rule
/// <c>NotEmptyValidator</c>, the rule an empty note already has).
/// </summary>
public sealed class AddAdjustmentValidator : BucketEntryValidator<AddAdjustment>
{
    /// <summary>Creates the rules.</summary>
    public AddAdjustmentValidator()
    {
        RuleFor(command => command.Direction).IsInEnum();

        // An empty note already fails the common note rules with this code; only a missing one is added here.
        RuleFor(command => command.Note).NotNull().WithErrorCode("NotEmptyValidator");
    }
}

/// <summary>Writes the adjustment through <see cref="BucketEntryWriter"/>; an adjustment has no rule of its own about the bucket.</summary>
public sealed class AddAdjustmentHandler(BucketEntryWriter writer) : ICommandHandler<AddAdjustment, Result>
{
    /// <inheritdoc />
    public ValueTask<Result> Handle(AddAdjustment command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return writer.WriteAsync(
            command,
            arguments => Adjustment.Create(
                    arguments.Id, arguments.OwnerId, arguments.BucketId, command.Amount, JournalContractMapping.ToDomain(command.Direction),
                    command.OccurredOn, arguments.Today, arguments.Info, arguments.RecordedAt)
                .Map(entry => (JournalEntry)entry),
            (_, _) => Result.Ok(),
            cancellationToken);
    }
}
