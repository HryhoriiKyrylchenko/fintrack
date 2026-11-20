using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.Settings;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Results;
using FluentResults;

namespace FinTrack.Budget.Application;

/// <summary>
/// The one place that reads Budget's state for a command or a query (ARCHITECTURE 2.3): the ledger
/// version first, then the documents. A command commits against <see cref="BudgetSnapshot.Version"/>,
/// so a write that lands after the read makes the commit fail instead of passing on stale data.
/// </summary>
/// <remarks>
/// Settings, buckets and journal are separate reads, so a commit can land between them: the journal
/// could then hold an entry for a bucket the bucket list did not have yet. To return one consistent
/// state, the loader reads the version again after the documents. Every commit raises the version in
/// the same transaction as its documents, so an unchanged version means no commit landed in between.
/// When it changed, the loader reads again, at most <see cref="MaxAttempts"/> times, and then fails with
/// <c>concurrency.conflict</c> (a command is retried by the pipeline; a query answers 409).
/// </remarks>
public sealed class BudgetStateLoader(ILedger ledger, IBudgetStore store)
{
    /// <summary>How many times the loader reads before it reports a conflict.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Reads the version, then the owner's documents, then the version again; one consistent state or a conflict.</summary>
    public async Task<Result<BudgetSnapshot>> LoadAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var version = await ledger.GetVersionAsync(BudgetLedger.Name, cancellationToken).ConfigureAwait(false);
            var settings = await store.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
            var buckets = await store.GetBucketsAsync(cancellationToken).ConfigureAwait(false);
            var journal = await store.GetJournalAsync(cancellationToken).ConfigureAwait(false);

            if (await ledger.GetVersionAsync(BudgetLedger.Name, cancellationToken).ConfigureAwait(false) == version)
            {
                return Result.Ok(new BudgetSnapshot(version, settings, buckets, journal));
            }
        }

        return Result.Fail<BudgetSnapshot>(AppError.Conflict(ErrorCodes.ConcurrencyConflict));
    }
}

/// <summary>Budget's state for one owner, as read by <see cref="BudgetStateLoader"/>: all documents from one point in time.</summary>
/// <param name="Version">The ledger version the documents belong to; the expected version of a commit.</param>
/// <param name="Settings">The owner's settings, or <c>null</c> before the first save.</param>
/// <param name="Buckets">All of the owner's buckets, archived ones included.</param>
/// <param name="Journal">The owner's journal, in replay order.</param>
public sealed record BudgetSnapshot(long Version, OwnerSettings? Settings, IReadOnlyList<Bucket> Buckets, IReadOnlyList<JournalEntry> Journal)
{
    private BudgetState? state;

    /// <summary>
    /// The state from replaying <see cref="Journal"/> for <paramref name="today"/> (plan 1b B20): today in the owner's
    /// time zone, which the handler computes from the clock and the settings. Replayed on first use and kept for that day.
    /// </summary>
    public BudgetState StateAsOf(DateOnly today)
    {
        if (state is not { } replayed || replayed.Today != today)
        {
            state = BudgetState.Replay(Buckets, Journal, today);
        }

        return state;
    }

    /// <summary>The owner's bucket with <paramref name="id"/>, or <c>null</c>.</summary>
    public Bucket? FindBucket(Guid id) => Buckets.FirstOrDefault(bucket => bucket.Id == id);

    /// <summary>The owner's <c>Unallocated</c> bucket of <paramref name="currency"/>, or <c>null</c> before its first use.</summary>
    public Bucket? FindUnallocated(Shared.Kernel.Monetary.Currency currency) =>
        Buckets.FirstOrDefault(bucket => bucket.SystemRole == BucketSystemRole.Unallocated && bucket.Currency == currency);

    /// <summary>The owner's entry with <paramref name="id"/>, or <c>null</c>.</summary>
    public JournalEntry? FindEntry(Guid id) => Journal.FirstOrDefault(entry => entry.Id == id);

    /// <summary>The owner's entries with <paramref name="groupId"/> (written by one command, plan 1b B15), possibly none.</summary>
    public IReadOnlyList<JournalEntry> FindGroup(Guid groupId) => [.. Journal.Where(entry => entry.GroupId == groupId)];
}
