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
/// The one write path of journal entries, for one entry or several written together (plan 1b B15). In this order:
/// client IDs (UUIDv7, the group's too); state (one consistent read, version first); settings (for "today"); the
/// command's draft (targets resolved, the entries built, their rules about the buckets); a repeat (D4, B15),
/// recognised before any rule because it was valid when written; the bucket rules; the entries' own rules;
/// invariants 4 and 1, in this order, on one replay that includes every new entry
/// (<see cref="BudgetErrorCodes.LoanRepaymentExceedsOutstanding"/>, plan 1b B3; <see cref="BudgetErrorCodes.BucketInsufficientFunds"/>,
/// D2; each with the first day it breaks); then one commit with the entries and any <c>Unallocated</c> bucket the draft created (D5).
/// </summary>
/// <remarks>
/// A repeat: when any of the command's entry IDs exists, or its group ID is already stored, the command writes
/// nothing if every entry exists with the same content (group included) and the stored group holds exactly these
/// entries; otherwise it fails with <see cref="BudgetErrorCodes.EntryIdReused"/>. So a changed entry, a part of a
/// stored group, a group extended by new entries and new entries under a stored group ID are all refused.
/// </remarks>
public sealed class JournalWriter(BudgetStateLoader loader, ILedger ledger, IOwnerContext owner, IClock clock)
{
    /// <summary>Writes the one entry that <paramref name="draft"/> makes, after every check.</summary>
    /// <param name="entryId">The command's entry ID.</param>
    /// <param name="draft">Resolves the targets and builds the entry; fails for an unknown bucket.</param>
    /// <param name="cancellationToken">Cancels the read and the commit.</param>
    public ValueTask<Result> WriteAsync(Guid entryId, Func<JournalWriteContext, Result<JournalDraft>> draft, CancellationToken cancellationToken) =>
        WriteAsync([entryId], groupId: null, draft, cancellationToken);

    /// <summary>Writes the entries that <paramref name="draft"/> makes, in one commit, after every check.</summary>
    /// <param name="entryIds">The command's entry IDs, one per entry, distinct, in the order the draft builds the entries.</param>
    /// <param name="groupId">The command's group ID; required for several entries (the validators require it), <c>null</c> or a group for one.</param>
    /// <param name="draft">Resolves the targets and builds the entries, each with <see cref="JournalWriteContext.GroupId"/>; fails for an unknown bucket.</param>
    /// <param name="cancellationToken">Cancels the read and the commit.</param>
    /// <exception cref="ArgumentException">No entry ID, a repeated entry ID, or several entries without a group (bugs: the validators keep them out).</exception>
    /// <exception cref="InvalidOperationException">The draft's entries do not have the given IDs, in order, and group (a bug).</exception>
    public async ValueTask<Result> WriteAsync(
        IReadOnlyList<Guid> entryIds,
        Guid? groupId,
        Func<JournalWriteContext, Result<JournalDraft>> draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        ArgumentNullException.ThrowIfNull(draft);
        CheckShape(entryIds, groupId);

        var ids = entryIds.Select(StronglyTypedId.FromClient<JournalEntryId>).ToList();
        if (ids.Exists(id => id.IsFailed) || (groupId is { } group && !UuidV7.IsValid(group)))
        {
            return Result.Fail(AppError.Validation(ErrorCodes.IdInvalid));
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
        var context = new JournalWriteContext(state, owner.OwnerId, [.. ids.Select(id => id.Value)], groupId, settings.Today(now), now);
        var drafted = draft(context);
        if (drafted.IsFailed)
        {
            return drafted.ToResult();
        }

        var (entries, rules) = drafted.Value;
        CheckDraft(entries, entryIds, groupId);

        if (IsRepeatCandidate(state, entryIds, groupId))
        {
            return IsRepeat(state, entries, entryIds, groupId)
                ? Result.Ok()
                : Result.Fail(AppError.Conflict(BudgetErrorCodes.EntryIdReused));
        }

        var checks = rules.Bind(() => Result.Merge([.. entries.Select(entry => entry.ToResult())]));
        if (checks.IsFailed)
        {
            return checks;
        }

        IReadOnlyList<JournalEntry> written = [.. entries.Select(entry => entry.Value)];
        var replayed = BudgetState.Replay([.. state.Buckets, .. context.NewBuckets], [.. state.Journal, .. written], context.Today);
        if (replayed.FirstOverRepayment is { } overRepayment)
        {
            return Result.Fail(AppError.BusinessRule(
                BudgetErrorCodes.LoanRepaymentExceedsOutstanding,
                ("date", overRepayment.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
        }

        if (replayed.FirstOverdraft is { } overdraft)
        {
            return Result.Fail(AppError.BusinessRule(
                BudgetErrorCodes.BucketInsufficientFunds,
                ("date", overdraft.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
        }

        var commit = new LedgerCommit(BudgetLedger.Name, state.Version, appends: written, saves: [.. context.NewBuckets]);
        return (await ledger.CommitAsync(commit, cancellationToken).ConfigureAwait(false)).ToResult();
    }

    private static void CheckShape(IReadOnlyList<Guid> entryIds, Guid? groupId)
    {
        if (entryIds.Count == 0)
        {
            throw new ArgumentException("A command writes at least one entry.", nameof(entryIds));
        }

        if (entryIds.Distinct().Count() != entryIds.Count)
        {
            throw new ArgumentException("Every entry of a command has its own ID; the validators reject a repeated one.", nameof(entryIds));
        }

        if (entryIds.Count > 1 && groupId is null)
        {
            throw new ArgumentException("A command that writes several entries carries a group ID; the validators require one.", nameof(groupId));
        }
    }

    // Every built entry has the ID at its position and the command's group: anything else is a bug in the command's draft.
    private static void CheckDraft(IReadOnlyList<Result<JournalEntry>> entries, IReadOnlyList<Guid> entryIds, Guid? groupId)
    {
        var matches = entries.Count == entryIds.Count
            && entries.Select((entry, index) => entry.IsFailed || (entry.Value.Id == entryIds[index] && entry.Value.GroupId == groupId)).All(match => match);
        if (!matches)
        {
            throw new InvalidOperationException("The draft's entries must have the command's entry IDs, in order, and its group.");
        }
    }

    private static bool IsRepeatCandidate(BudgetSnapshot state, IReadOnlyList<Guid> entryIds, Guid? groupId) =>
        entryIds.Any(id => state.FindEntry(id) is not null) || (groupId is { } group && state.FindGroup(group).Count > 0);

    // The same command again: every entry stored with the same content (its group included), and nothing else in the group.
    private static bool IsRepeat(BudgetSnapshot state, IReadOnlyList<Result<JournalEntry>> entries, IReadOnlyList<Guid> entryIds, Guid? groupId) =>
        entries.Select((entry, index) => entry.IsSuccess && state.FindEntry(entryIds[index]) is { } stored && stored.HasSameContent(entry.Value)).All(same => same)
        && (groupId is not { } group || state.FindGroup(group).Count == entryIds.Count);
}

/// <summary>What a command's draft gets: the state, the owner, the entries' IDs and group, today, now, and target resolution.</summary>
public sealed class JournalWriteContext
{
    private readonly List<Bucket> newBuckets = [];

    internal JournalWriteContext(BudgetSnapshot state, OwnerId owner, IReadOnlyList<JournalEntryId> entryIds, Guid? groupId, DateOnly today, DateTimeOffset now)
    {
        State = state;
        Owner = owner;
        EntryIds = entryIds;
        GroupId = groupId;
        Today = today;
        Now = now;
    }

    /// <summary>The state the command is checked against.</summary>
    public BudgetSnapshot State { get; }

    /// <summary>The balances of <see cref="State"/> as of <see cref="Today"/>.</summary>
    public BudgetState Balances => State.StateAsOf(Today);

    /// <summary>The owner.</summary>
    public OwnerId Owner { get; }

    /// <summary>The new entries' IDs, in the order the draft builds the entries.</summary>
    public IReadOnlyList<JournalEntryId> EntryIds { get; }

    /// <summary>The new entry's ID, for a command that writes one entry.</summary>
    /// <exception cref="InvalidOperationException">The command writes several entries (use <see cref="EntryIds"/>).</exception>
    public JournalEntryId EntryId => EntryIds.Count == 1
        ? EntryIds[0]
        : throw new InvalidOperationException("This command writes several entries; use EntryIds.");

    /// <summary>The command's group, set on each of its entries; <c>null</c> for an entry written alone.</summary>
    public Guid? GroupId { get; }

    /// <summary>Today in the owner's time zone.</summary>
    public DateOnly Today { get; }

    /// <summary>Now, from the clock: the entries' <c>RecordedAt</c>.</summary>
    public DateTimeOffset Now { get; }

    /// <summary><c>Unallocated</c> buckets created by <see cref="ResolveTarget"/>; written in the same commit as the entries.</summary>
    public IReadOnlyList<Bucket> NewBuckets => newBuckets;

    /// <summary>
    /// The bucket a command names: by ID (<see cref="BudgetErrorCodes.BucketNotFound"/> when the owner has
    /// none), or the <c>Unallocated</c> bucket of a currency, created when it does not exist yet (one per
    /// currency, also when several targets of one command name it).
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

/// <summary>A command's draft: the entries it would write (or why each cannot be built) and its rules about the buckets.</summary>
/// <param name="Entries">One per entry ID of the command, in order: the built entry, or the entry's own rule failures.</param>
/// <param name="Rules">The bucket rules (archived, currency, the type's own rule).</param>
public sealed record JournalDraft(IReadOnlyList<Result<JournalEntry>> Entries, Result Rules)
{
    /// <summary>The draft of a command that writes one entry.</summary>
    /// <param name="entry">The built entry, or its own rule failures.</param>
    /// <param name="rules">The bucket rules.</param>
    public JournalDraft(Result<JournalEntry> entry, Result rules)
        : this([entry], rules)
    {
    }
}
