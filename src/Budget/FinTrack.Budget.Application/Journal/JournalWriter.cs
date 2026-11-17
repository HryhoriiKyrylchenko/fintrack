using System.Globalization;
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

namespace FinTrack.Budget.Application.Journal;

/// <summary>
/// The one write path of journal entries. In this order: client ID (UUIDv7); state (one consistent read,
/// version first); settings (for "today"); the command's draft (targets resolved, the entry built, its
/// rules about the buckets); a repeat with the same ID (same content writes nothing, other content
/// <see cref="BudgetErrorCodes.EntryIdReused"/>, D4), recognised before any rule because it was valid when
/// written; the bucket rules; the entry's own rules; invariant 1 on the replay that includes the new entry
/// (<see cref="BudgetErrorCodes.BucketInsufficientFunds"/>, D2); then one commit with the entry and any
/// <c>Unallocated</c> bucket the draft created (D5).
/// </summary>
public sealed class JournalWriter(BudgetStateLoader loader, ILedger ledger, IOwnerContext owner, IClock clock)
{
    /// <summary>Writes the entry that <paramref name="draft"/> makes, after every check.</summary>
    /// <param name="entryId">The command's entry ID.</param>
    /// <param name="draft">Resolves the targets and builds the entry; fails for an unknown bucket.</param>
    /// <param name="cancellationToken">Cancels the read and the commit.</param>
    public async ValueTask<Result> WriteAsync(Guid entryId, Func<JournalWriteContext, Result<JournalDraft>> draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var id = StronglyTypedId.FromClient<JournalEntryId>(entryId);
        if (id.IsFailed)
        {
            return id.ToResult();
        }

        var loaded = await loader.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailed)
        {
            return loaded.ToResult();
        }

        var state = loaded.Value;
        if (state.Settings is not { } settings)
        {
            return Result.Fail(AppError.NotFound(BudgetErrorCodes.SettingsNotFound));
        }

        var now = clock.UtcNow;
        var context = new JournalWriteContext(state, owner.OwnerId, id.Value, settings.Today(now), now);
        var drafted = draft(context);
        if (drafted.IsFailed)
        {
            return drafted.ToResult();
        }

        var (entry, rules) = drafted.Value;

        if (state.FindEntry(entryId) is { } existing)
        {
            return entry.IsSuccess && existing.HasSameContent(entry.Value)
                ? Result.Ok()
                : Result.Fail(AppError.Conflict(BudgetErrorCodes.EntryIdReused));
        }

        var checks = rules.Bind(() => entry.ToResult());
        if (checks.IsFailed)
        {
            return checks;
        }

        if (BudgetState.Replay([.. state.Buckets, .. context.NewBuckets], [.. state.Journal, entry.Value]).FirstOverdraft is { } overdraft)
        {
            return Result.Fail(AppError.BusinessRule(
                BudgetErrorCodes.BucketInsufficientFunds,
                ("date", overdraft.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
        }

        var commit = new LedgerCommit(BudgetLedger.Name, state.Version, appends: [entry.Value], saves: [.. context.NewBuckets]);
        return (await ledger.CommitAsync(commit, cancellationToken).ConfigureAwait(false)).ToResult();
    }
}

/// <summary>What a command's draft gets: the state, the owner, the entry's ID, today, now, and target resolution.</summary>
public sealed class JournalWriteContext
{
    private readonly List<Bucket> newBuckets = [];

    internal JournalWriteContext(BudgetSnapshot state, OwnerId owner, JournalEntryId entryId, DateOnly today, DateTimeOffset now)
    {
        State = state;
        Owner = owner;
        EntryId = entryId;
        Today = today;
        Now = now;
    }

    /// <summary>The state the command is checked against.</summary>
    public BudgetSnapshot State { get; }

    /// <summary>The owner.</summary>
    public OwnerId Owner { get; }

    /// <summary>The new entry's ID.</summary>
    public JournalEntryId EntryId { get; }

    /// <summary>Today in the owner's time zone.</summary>
    public DateOnly Today { get; }

    /// <summary>Now, from the clock: the entry's <c>RecordedAt</c>.</summary>
    public DateTimeOffset Now { get; }

    /// <summary><c>Unallocated</c> buckets created by <see cref="ResolveTarget"/>; written in the same commit as the entry.</summary>
    public IReadOnlyList<Bucket> NewBuckets => newBuckets;

    /// <summary>
    /// The bucket a command names: by ID (<see cref="BudgetErrorCodes.BucketNotFound"/> when the owner has
    /// none), or the <c>Unallocated</c> bucket of a currency, created when it does not exist yet (one per
    /// currency, also when both sides of a transfer name it).
    /// </summary>
    public Result<Bucket> ResolveTarget(Guid? bucketId, Currency? unallocated)
    {
        if (bucketId is { } id)
        {
            return State.FindBucket(id) is { } bucket
                ? Result.Ok(bucket)
                : Result.Fail<Bucket>(AppError.NotFound(BudgetErrorCodes.BucketNotFound));
        }

        var currency = unallocated ?? throw new InvalidOperationException("A target needs a bucket ID or a currency; the validators require one.");
        if ((State.FindUnallocated(currency) ?? newBuckets.Find(bucket => bucket.Currency == currency)) is { } existing)
        {
            return Result.Ok(existing);
        }

        var created = Bucket.Unallocated(BucketId.From(Guid.CreateVersion7(Now)), Owner, currency);
        newBuckets.Add(created);
        return Result.Ok(created);
    }

    /// <summary>The rules every target bucket has: active (<see cref="BudgetErrorCodes.BucketArchived"/>) and in the amount's currency (<see cref="BudgetErrorCodes.BucketCurrencyMismatch"/>).</summary>
    public static Result CheckBucket(Bucket bucket, Money amount)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        ArgumentNullException.ThrowIfNull(amount);

        if (bucket.Status == BucketStatus.Archived)
        {
            return Result.Fail(AppError.BusinessRule(BudgetErrorCodes.BucketArchived));
        }

        return bucket.Currency != amount.Currency
            ? Result.Fail(AppError.BusinessRule(BudgetErrorCodes.BucketCurrencyMismatch))
            : Result.Ok();
    }
}

/// <summary>A command's draft: the entry it would write (or why it cannot be built) and its rules about the buckets.</summary>
/// <param name="Entry">The built entry, or the entry's own rule failures.</param>
/// <param name="Rules">The bucket rules (archived, currency, the type's own rule).</param>
public sealed record JournalDraft(Result<JournalEntry> Entry, Result Rules);
