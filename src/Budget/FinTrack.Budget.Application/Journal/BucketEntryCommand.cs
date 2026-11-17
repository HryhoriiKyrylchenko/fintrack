using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Persistence;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Shared.Kernel.Time;
using FluentResults;
using FluentValidation;

namespace FinTrack.Budget.Application.Journal;

/// <summary>
/// A command that writes one single-bucket entry (<c>AddOpeningBalance</c>, <c>AddDeposit</c>,
/// <c>AddSpend</c>). The target is a bucket by ID or the <c>Unallocated</c> bucket of a currency.
/// </summary>
public interface IBucketEntryCommand
{
    /// <summary>The entry's ID, a version 7 UUID from the client.</summary>
    Guid Id { get; }

    /// <summary>Strictly positive, in the target's currency.</summary>
    Money Amount { get; }

    /// <summary>The business date.</summary>
    DateOnly OccurredOn { get; }

    /// <summary>The target bucket, or <c>null</c> when <see cref="Unallocated"/> is set.</summary>
    Guid? BucketId { get; }

    /// <summary>The currency of the target <c>Unallocated</c> bucket, or <c>null</c>.</summary>
    Currency? Unallocated { get; }

    /// <summary>An optional note.</summary>
    string? Note { get; }

    /// <summary>Tags, possibly none.</summary>
    IReadOnlyList<string> Tags { get; }
}

/// <summary>The shape checks every single-bucket entry command shares; the rules that need the state are in the write path.</summary>
/// <typeparam name="TCommand">The command.</typeparam>
public abstract class BucketEntryValidator<TCommand> : AbstractValidator<TCommand>
    where TCommand : IBucketEntryCommand
{
    /// <summary>The rule code for a command with both or neither of <c>bucketId</c> and <c>unallocated</c>.</summary>
    public const string ExactlyOneTarget = "ExactlyOneTarget";

    /// <summary>Creates the rules.</summary>
    protected BucketEntryValidator()
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
/// The one write path of single-bucket entries. In this order: client ID (UUIDv7); state, version
/// first; settings (for "today"); the target (an <c>Unallocated</c> bucket is created when missing,
/// written only with the entry, D5); a repeat with the same ID (same content writes nothing, other
/// content <see cref="BudgetErrorCodes.EntryIdReused"/>, D4); archived bucket; currency; the entry
/// type's own rule; the entry's rules (amount, date); invariant 1 on the replay that includes the new
/// entry (<see cref="BudgetErrorCodes.BucketInsufficientFunds"/>, D2); then one commit.
/// </summary>
public sealed class BucketEntryWriter(BudgetStateLoader loader, ILedger ledger, IOwnerContext owner, IClock clock)
{
    /// <summary>What the write path passes to the entry type's factory.</summary>
    /// <param name="Id">The entry's ID.</param>
    /// <param name="OwnerId">The owner.</param>
    /// <param name="BucketId">The resolved target.</param>
    /// <param name="Today">Today in the owner's time zone.</param>
    /// <param name="Info">Note, tags and source.</param>
    /// <param name="RecordedAt">Now.</param>
    public sealed record EntryArguments(JournalEntryId Id, OwnerId OwnerId, BucketId BucketId, DateOnly Today, EntryInfo Info, DateTimeOffset RecordedAt);

    /// <summary>Writes the entry that <paramref name="create"/> makes, after every check.</summary>
    /// <param name="command">The command.</param>
    /// <param name="create">The entry type's factory.</param>
    /// <param name="typeRule">The entry type's own rule about the target bucket in the current state (for example "no spend from an investment bucket"); ok when it allows the entry.</param>
    /// <param name="cancellationToken">Cancels the read and the commit.</param>
    public async ValueTask<Result> WriteAsync(
        IBucketEntryCommand command,
        Func<EntryArguments, Result<JournalEntry>> create,
        Func<Bucket, BudgetSnapshot, Result> typeRule,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(typeRule);

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
        var entry = create(new EntryArguments(id.Value, owner.OwnerId, bucket.BucketId, settings.Today(now), new EntryInfo(command.Note, command.Tags, EntrySource.Manual), now));

        // A repeat is recognised before any rule: it was valid when it was written.
        if (state.FindEntry(command.Id) is { } existing)
        {
            return entry.IsSuccess && existing.HasSameContent(entry.Value)
                ? Result.Ok()
                : Result.Fail(AppError.Conflict(BudgetErrorCodes.EntryIdReused));
        }

        var rules = CheckBucket(bucket, command.Amount)
            .Bind(() => typeRule(bucket, state))
            .Bind(() => entry.ToResult());
        if (rules.IsFailed)
        {
            return rules;
        }

        var buckets = isNew ? [.. state.Buckets, bucket] : state.Buckets;
        if (BudgetState.Replay(buckets, [.. state.Journal, entry.Value]).FirstOverdraft is { } overdraft)
        {
            return Result.Fail(AppError.BusinessRule(BudgetErrorCodes.BucketInsufficientFunds, ("date", overdraft.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))));
        }

        var commit = new LedgerCommit(BudgetLedger.Name, state.Version, appends: [entry.Value], saves: isNew ? [bucket] : null);
        return (await ledger.CommitAsync(commit, cancellationToken).ConfigureAwait(false)).ToResult();
    }

    private static Result CheckBucket(Bucket bucket, Money amount)
    {
        if (bucket.Status == BucketStatus.Archived)
        {
            return Result.Fail(AppError.BusinessRule(BudgetErrorCodes.BucketArchived));
        }

        return bucket.Currency != amount.Currency
            ? Result.Fail(AppError.BusinessRule(BudgetErrorCodes.BucketCurrencyMismatch))
            : Result.Ok();
    }

    private Result<(Bucket Bucket, bool IsNew)> ResolveTarget(IBucketEntryCommand command, BudgetSnapshot state, DateTimeOffset now)
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
            : Result.Ok((Bucket.Unallocated(Domain.Buckets.BucketId.From(Guid.CreateVersion7(now)), owner.OwnerId, currency), true));
    }
}
