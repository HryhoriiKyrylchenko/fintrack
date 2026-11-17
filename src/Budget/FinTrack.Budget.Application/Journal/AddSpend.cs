using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FluentResults;
using Mediator;

namespace FinTrack.Budget.Application.Journal;

/// <summary>
/// Money that leaves a savings bucket for good (<c>POST /api/v1/journal/spends</c>). Not allowed on an
/// investment bucket (<see cref="BudgetErrorCodes.BucketSpendNotAllowed"/>, D11); never below zero at any
/// point of the replay order (<see cref="BudgetErrorCodes.BucketInsufficientFunds"/>, D2). Target,
/// repeats and the other rules as for <see cref="AddDeposit"/>.
/// </summary>
/// <param name="Id">The entry's ID, a version 7 UUID from the client.</param>
/// <param name="Amount">Strictly positive, in the bucket's currency.</param>
/// <param name="OccurredOn">The business date.</param>
/// <param name="BucketId">The bucket, or <c>null</c> when <paramref name="Unallocated"/> is set.</param>
/// <param name="Unallocated">The currency of the <c>Unallocated</c> bucket the money leaves, or <c>null</c>.</param>
/// <param name="Note">An optional note.</param>
/// <param name="Tags">Tags, possibly none.</param>
public sealed record AddSpend(
    Guid Id,
    Money Amount,
    DateOnly OccurredOn,
    Guid? BucketId,
    Currency? Unallocated,
    string? Note,
    IReadOnlyList<string> Tags) : ICommand<Result>, ILedgerCommand, IBucketEntryCommand;

/// <summary>Shape checks (<see cref="BucketEntryValidator{TCommand}"/>).</summary>
public sealed class AddSpendValidator : BucketEntryValidator<AddSpend>;

/// <summary>Writes the spend through <see cref="BucketEntryWriter"/>, rejecting investment buckets.</summary>
public sealed class AddSpendHandler(BucketEntryWriter writer) : ICommandHandler<AddSpend, Result>
{
    /// <inheritdoc />
    public ValueTask<Result> Handle(AddSpend command, CancellationToken cancellationToken) =>
        writer.WriteAsync(
            command,
            arguments => Spend.Create(arguments.Id, arguments.OwnerId, arguments.BucketId, command.Amount, command.OccurredOn, arguments.Today, arguments.Info, arguments.RecordedAt)
                .Map(entry => (JournalEntry)entry),
            (bucket, _) => bucket.Kind == BucketKind.Investment
                ? Result.Fail(AppError.BusinessRule(BudgetErrorCodes.BucketSpendNotAllowed))
                : Result.Ok(),
            cancellationToken);
}
