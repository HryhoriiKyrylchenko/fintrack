using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Persistence;
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
/// The write path of single-bucket entries, on <see cref="JournalWriter"/>: one target, the bucket rules
/// (archived, currency), then the entry type's own rule.
/// </summary>
public sealed class BucketEntryWriter(BudgetStateLoader loader, ILedger ledger, IOwnerContext owner, IClock clock)
{
    private readonly JournalWriter writer = new(loader, ledger, owner, clock);

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
    /// <param name="typeRule">The entry type's own rule about the target bucket in the state the write is checked against (for example "no spend from an investment bucket"); ok when it allows the entry.</param>
    /// <param name="cancellationToken">Cancels the read and the commit.</param>
    public ValueTask<Result> WriteAsync(
        IBucketEntryCommand command,
        Func<EntryArguments, Result<JournalEntry>> create,
        Func<Bucket, JournalWriteContext, Result> typeRule,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(typeRule);

        return writer.WriteAsync(
            command.Id,
            context => context.ResolveTarget(command.BucketId, command.Unallocated).Map(bucket => new JournalDraft(
                create(new EntryArguments(context.EntryId, context.Owner, bucket.BucketId, context.Today, new EntryInfo(command.Note, command.Tags, EntrySource.Manual), context.Now)),
                JournalWriteContext.CheckBucket(bucket, command.Amount).Bind(() => typeRule(bucket, context)))),
            cancellationToken);
    }
}
