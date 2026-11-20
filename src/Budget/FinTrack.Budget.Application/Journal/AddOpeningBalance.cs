using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FluentResults;
using Mediator;

namespace FinTrack.Budget.Application.Journal;

/// <summary>
/// The amount a bucket held when the owner started (<c>POST /api/v1/journal/opening-balances</c>):
/// usually backdated, not income. A bucket has at most one (<see cref="BudgetErrorCodes.BucketOpeningBalanceExists"/>, D13).
/// Target, repeats and the other rules as for <see cref="AddDeposit"/>.
/// </summary>
/// <param name="Id">The entry's ID, a version 7 UUID from the client.</param>
/// <param name="Amount">Strictly positive, in the target's currency.</param>
/// <param name="OccurredOn">The business date.</param>
/// <param name="BucketId">The target bucket, or <c>null</c> when <paramref name="Unallocated"/> is set.</param>
/// <param name="Unallocated">The currency of the target <c>Unallocated</c> bucket, or <c>null</c>.</param>
/// <param name="Note">An optional note.</param>
/// <param name="Tags">Tags, possibly none.</param>
public sealed record AddOpeningBalance(
    Guid Id,
    Money Amount,
    DateOnly OccurredOn,
    Guid? BucketId,
    Currency? Unallocated,
    string? Note,
    IReadOnlyList<string> Tags) : ICommand<Result>, ILedgerCommand, IBucketEntryCommand;

/// <summary>Shape checks (<see cref="BucketEntryValidator{TCommand}"/>).</summary>
public sealed class AddOpeningBalanceValidator : BucketEntryValidator<AddOpeningBalance>;

/// <summary>Writes the opening balance through <see cref="BucketEntryWriter"/>, rejecting a second one for the bucket.</summary>
public sealed class AddOpeningBalanceHandler(BucketEntryWriter writer) : ICommandHandler<AddOpeningBalance, Result>
{
    /// <inheritdoc />
    public ValueTask<Result> Handle(AddOpeningBalance command, CancellationToken cancellationToken) =>
        writer.WriteAsync(
            command,
            arguments => OpeningBalance.Create(arguments.Id, arguments.OwnerId, arguments.BucketId, command.Amount, command.OccurredOn, arguments.Today, arguments.Info, arguments.RecordedAt)
                .Map(entry => (JournalEntry)entry),
            (bucket, context) => context.State.Buckets.Contains(bucket) && context.Balances.BalanceOf(bucket.BucketId).HasOpeningBalance
                ? Result.Fail(AppError.BusinessRule(BudgetErrorCodes.BucketOpeningBalanceExists))
                : Result.Ok(),
            cancellationToken);
}
