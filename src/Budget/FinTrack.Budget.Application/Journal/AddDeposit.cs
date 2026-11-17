using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Monetary;
using FluentResults;
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
public sealed record AddDeposit(
    Guid Id,
    Money Amount,
    DateOnly OccurredOn,
    Guid? BucketId,
    Currency? Unallocated,
    string? Note,
    IReadOnlyList<string> Tags) : ICommand<Result>, ILedgerCommand, IBucketEntryCommand;

/// <summary>Shape checks (<see cref="BucketEntryValidator{TCommand}"/>).</summary>
public sealed class AddDepositValidator : BucketEntryValidator<AddDeposit>;

/// <summary>Writes the deposit through <see cref="BucketEntryWriter"/>; a deposit has no rule of its own.</summary>
public sealed class AddDepositHandler(BucketEntryWriter writer) : ICommandHandler<AddDeposit, Result>
{
    /// <inheritdoc />
    public ValueTask<Result> Handle(AddDeposit command, CancellationToken cancellationToken) =>
        writer.WriteAsync(
            command,
            arguments => Deposit.Create(arguments.Id, arguments.OwnerId, arguments.BucketId, command.Amount, command.OccurredOn, arguments.Today, arguments.Info, arguments.RecordedAt)
                .Map(entry => (JournalEntry)entry),
            (_, _) => Result.Ok(),
            cancellationToken);
}
