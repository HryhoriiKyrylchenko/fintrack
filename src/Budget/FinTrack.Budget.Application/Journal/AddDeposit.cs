using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Persistence;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Shared.Kernel.Time;
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
public sealed record AddDeposit(
    Guid Id,
    Money Amount,
    DateOnly OccurredOn,
    Guid? BucketId,
    Currency? Unallocated,
    string? Note,
    IReadOnlyList<string> Tags) : ICommand<Result>, ILedgerCommand;

/// <summary>Shape checks; the rules that need the state are in the handler and the domain.</summary>
public sealed class AddDepositValidator : AbstractValidator<AddDeposit>
{
    /// <summary>The rule code for a deposit with both or neither of <c>bucketId</c> and <c>unallocated</c>.</summary>
    public const string ExactlyOneTarget = "ExactlyOneTarget";

    /// <summary>Creates the rules.</summary>
    public AddDepositValidator()
    {
        RuleFor(command => command.Amount).NotNull();
        RuleFor(command => command.BucketId)
            .Must((command, bucketId) => (bucketId is null) != (command.Unallocated is null))
            .WithErrorCode(ExactlyOneTarget);
        RuleFor(command => command.Note).EntryNote();
        RuleFor(command => command.Tags).EntryTags();
        RuleForEach(command => command.Tags).EntryTag();
    }
}

/// <summary>
/// Reads the state (version first), resolves the target, recognises a repeat, checks the rules and
/// appends the deposit (with a new <c>Unallocated</c> bucket when needed) under the version it read.
/// </summary>
public sealed class AddDepositHandler(BudgetStateLoader loader, ILedger ledger, IOwnerContext owner, IClock clock) : ICommandHandler<AddDeposit, Result>
{
    /// <inheritdoc />
    public async ValueTask<Result> Handle(AddDeposit command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var id = StronglyTypedId.FromClient<JournalEntryId>(command.Id);
        if (id.IsFailed)
        {
            return id.ToResult();
        }

        var state = await loader.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (state.Settings is not { } settings)
        {
            return Result.Fail(AppError.NotFound(BudgetErrorCodes.SettingsNotFound));
        }

        var now = clock.UtcNow;
        var target = ResolveTarget(command, state, now);
        if (target.IsFailed)
        {
            return target.ToResult();
        }

        var (bucket, isNew) = target.Value;
        var deposit = Deposit.Create(
            id.Value, owner.OwnerId, bucket.BucketId, command.Amount, command.OccurredOn, settings.Today(now),
            new EntryInfo(command.Note, command.Tags, EntrySource.Manual), now);

        // A repeat is recognised before any rule: it was valid when it was written.
        if (state.FindEntry(command.Id) is { } existing)
        {
            return deposit.IsSuccess && existing.HasSameContent(deposit.Value)
                ? Result.Ok()
                : Result.Fail(AppError.Conflict(BudgetErrorCodes.EntryIdReused));
        }

        if (bucket.Status == BucketStatus.Archived)
        {
            return Result.Fail(AppError.BusinessRule(BudgetErrorCodes.BucketArchived));
        }

        if (bucket.Currency != command.Amount.Currency)
        {
            return Result.Fail(AppError.BusinessRule(BudgetErrorCodes.BucketCurrencyMismatch));
        }

        if (deposit.IsFailed)
        {
            return deposit.ToResult();
        }

        var commit = new LedgerCommit(BudgetLedger.Name, state.Version, appends: [deposit.Value], saves: isNew ? [bucket] : null);
        return (await ledger.CommitAsync(commit, cancellationToken).ConfigureAwait(false)).ToResult();
    }

    private Result<(Bucket Bucket, bool IsNew)> ResolveTarget(AddDeposit command, BudgetSnapshot state, DateTimeOffset now)
    {
        if (command.BucketId is { } bucketId)
        {
            return state.FindBucket(bucketId) is { } bucket
                ? Result.Ok((bucket, false))
                : Result.Fail<(Bucket, bool)>(AppError.NotFound(BudgetErrorCodes.BucketNotFound));
        }

        var currency = command.Unallocated ?? throw new InvalidOperationException("The validator requires exactly one target.");
        return state.FindUnallocated(currency) is { } unallocated
            ? Result.Ok((unallocated, false))
            : Result.Ok((Bucket.Unallocated(BucketId.From(Guid.CreateVersion7(now)), owner.OwnerId, currency), true));
    }
}
