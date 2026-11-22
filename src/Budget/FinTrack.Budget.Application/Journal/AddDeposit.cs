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
/// New money into one bucket (<c>POST /api/v1/journal/deposits</c>). The target is a bucket by ID or the
/// <c>Unallocated</c> bucket of a currency, created in the same commit when it does not exist yet (D5).
/// A repeat with the same ID and content writes nothing; with other content it fails with
/// <see cref="BudgetErrorCodes.EntryIdReused"/> (invariant 13, D4).
/// </summary>
/// <param name="Id">The entry's ID, a version 7 UUID from the client.</param>
/// <param name="Amount">Strictly positive, in the target's currency.</param>
/// <param name="OccurredOn">The business date.</param>
/// <param name="BucketId">The target bucket, or <c>null</c> when <paramref name="Unallocated"/> is set.</param>
/// <param name="Unallocated">The currency of the target <c>Unallocated</c> bucket, or <c>null</c>.</param>
/// <param name="Note">An optional note.</param>
/// <param name="Tags">Tags, possibly none.</param>
/// <param name="PlanId">
/// The contribution plan of the receiving bucket that the deposit pays (plan 1b B9), or <c>null</c> for a one-time deposit; fails with
/// <see cref="BudgetErrorCodes.PlanNotFound"/> (404) or <see cref="BudgetErrorCodes.PlanBucketMismatch"/> (422).
/// </param>
public sealed record AddDeposit(
    Guid Id,
    Money Amount,
    DateOnly OccurredOn,
    Guid? BucketId,
    Currency? Unallocated,
    string? Note,
    IReadOnlyList<string> Tags,
    Guid? PlanId = null) : ICommand<Result>, ILedgerCommand, IBucketEntryCommand;

/// <summary>Shape checks (<see cref="BucketEntryValidator{TCommand}"/>) and a plan ID that is not empty when set.</summary>
public sealed class AddDepositValidator : BucketEntryValidator<AddDeposit>
{
    /// <summary>Creates the rules.</summary>
    public AddDepositValidator() => RuleFor(command => command.PlanId).NotEqual(Guid.Empty);
}

/// <summary>Writes the deposit through <see cref="BucketEntryWriter"/>; its own rule is its plan's (B9).</summary>
public sealed class AddDepositHandler(BucketEntryWriter writer) : ICommandHandler<AddDeposit, Result>
{
    /// <inheritdoc />
    public ValueTask<Result> Handle(AddDeposit command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return writer.WriteAsync(
            command,
            arguments => Deposit.Create(
                    arguments.Id, arguments.OwnerId, arguments.BucketId, command.Amount, command.OccurredOn, arguments.Today, arguments.Info, arguments.RecordedAt,
                    command.PlanId is { } planId ? Domain.Plans.PlanId.From(planId) : null)
                .Map(entry => (JournalEntry)entry),
            (bucket, context) => PlanRules.CheckContribution(command.PlanId, bucket, context.Balances),
            cancellationToken);
    }
}
