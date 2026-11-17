using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.Settings;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Results;
using FluentResults;

namespace FinTrack.Budget.Application.Tests.Histories;

/// <summary>
/// One owner's Budget kept in memory, acting as both the ledger and the store, so the real handlers run
/// against it unchanged. It behaves like the MongoDB adapter where the handlers can tell: a commit needs
/// the expected version, an ID that exists already is a conflict (the unique <c>_id</c>), a mutable
/// document is stored with the next version (as the real store stamps it) and replaced only when the
/// version it was read with still matches, and the journal is read in replay order.
/// </summary>
internal sealed class InMemoryBudget : ILedger, IBudgetStore
{
    private readonly List<JournalEntry> journal = [];
    private readonly List<Bucket> buckets = [];
    private OwnerSettings? settings;

    public long Version { get; private set; }

    public IReadOnlyList<JournalEntry> Journal => journal;

    public IReadOnlyList<Bucket> Buckets => buckets;

    public Task<long> GetVersionAsync(LedgerName ledger, CancellationToken cancellationToken) => Task.FromResult(Version);

    public Task<Result<LedgerCommitted>> CommitAsync(LedgerCommit commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);

        var reusedId = commit.Appends.Any(entry => journal.Exists(stored => stored.Id == entry.Id))
            || commit.Saves.Any(document => document.Version == 0 && (buckets.Exists(bucket => bucket.Id == document.Id) || settings?.Id == document.Id));
        var staleReplace = commit.Saves.Any(document => document.Version > 0
            && StoredVersion(document.Id) != document.Version);
        if (commit.ExpectedVersion != Version || reusedId || staleReplace)
        {
            return Task.FromResult(Result.Fail<LedgerCommitted>(AppError.Conflict(ErrorCodes.ConcurrencyConflict)));
        }

        journal.AddRange(commit.Appends.Cast<JournalEntry>());
        foreach (var document in commit.Saves)
        {
            switch (document)
            {
                case Bucket bucket:
                    var position = buckets.FindIndex(stored => stored.Id == bucket.Id);
                    var stamped = NextVersion(bucket);
                    if (position < 0)
                    {
                        buckets.Add(stamped);
                    }
                    else
                    {
                        buckets[position] = stamped;
                    }

                    break;
                case OwnerSettings saved:
                    settings = NextVersion(saved);
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected document {document.GetType().Name}.");
            }
        }

        Version++;
        return Task.FromResult(Result.Ok(new LedgerCommitted(Version, AlreadyHandled: false)));
    }

    private long? StoredVersion(Guid id) =>
        buckets.Find(bucket => bucket.Id == id)?.Version ?? (settings?.Id == id ? settings.Version : null);

    // The real store writes version + 1 and the read returns it; the domain objects themselves never change,
    // so the copy is made through the private constructor, as the database mapping does.
    private static T NextVersion<T>(T document)
        where T : class, Shared.Kernel.Persistence.IMutableDocument
    {
        var constructor = typeof(T).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single();
        var arguments = constructor.GetParameters()
            .Select(parameter => parameter.Name == "version"
                ? document.Version + 1
                : typeof(T).GetProperties().Single(property => string.Equals(property.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)).GetValue(document))
            .ToArray();
        return (T)constructor.Invoke(arguments);
    }

    public Task<OwnerSettings?> GetSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(settings);

    public Task<IReadOnlyList<Bucket>> GetBucketsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Bucket>>([.. buckets]);

    public Task<IReadOnlyList<JournalEntry>> GetJournalAsync(CancellationToken cancellationToken) => Task.FromResult(ReplayOrder.Sort(journal));
}
