using System.Reflection;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.Settings;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Persistence;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;
using FluentResults;

namespace FinTrack.Budget.Application.Tests;

/// <summary>The order in which a handler touched its dependencies.</summary>
internal sealed class CallLog
{
    public List<string> Calls { get; } = [];
}

/// <summary>An in-memory ledger: a version, the commits it received, and an optional conflict.</summary>
internal sealed class FakeLedger(CallLog log) : ILedger
{
    public long Version { get; set; }

    public bool Conflict { get; set; }

    public List<LedgerCommit> Commits { get; } = [];

    public Task<long> GetVersionAsync(LedgerName ledger, CancellationToken cancellationToken)
    {
        log.Calls.Add("version");
        return Task.FromResult(Version);
    }

    public Task<Result<LedgerCommitted>> CommitAsync(LedgerCommit commit, CancellationToken cancellationToken)
    {
        log.Calls.Add("commit");

        if (Conflict || commit.ExpectedVersion != Version)
        {
            return Task.FromResult(Result.Fail<LedgerCommitted>(AppError.Conflict(ErrorCodes.ConcurrencyConflict)));
        }

        Commits.Add(commit);
        Version++;
        return Task.FromResult(Result.Ok(new LedgerCommitted(Version, AlreadyHandled: false)));
    }
}

/// <summary>An in-memory Budget store.</summary>
internal sealed class FakeBudgetStore(CallLog log) : IBudgetStore
{
    public OwnerSettings? Settings { get; set; }

    public List<Bucket> Buckets { get; } = [];

    public Task<OwnerSettings?> GetSettingsAsync(CancellationToken cancellationToken)
    {
        log.Calls.Add("settings");
        return Task.FromResult(Settings);
    }

    public Task<IReadOnlyList<Bucket>> GetBucketsAsync(CancellationToken cancellationToken)
    {
        log.Calls.Add("buckets");
        return Task.FromResult<IReadOnlyList<Bucket>>([.. Buckets]);
    }

    public List<JournalEntry> Journal { get; } = [];

    /// <summary>Runs after the journal is read: a test can make a commit "land" between the reads.</summary>
    public Action? AfterJournalRead { get; set; }

    public Task<IReadOnlyList<JournalEntry>> GetJournalAsync(CancellationToken cancellationToken)
    {
        log.Calls.Add("journal");
        IReadOnlyList<JournalEntry> journal = [.. Journal];
        AfterJournalRead?.Invoke();
        return Task.FromResult(journal);
    }
}

internal sealed class FakeOwnerContext : IOwnerContext
{
    public static readonly OwnerId Owner = OwnerId.From(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));

    public OwnerId OwnerId => Owner;
}

/// <summary>Builds buckets as the database returns them (through the private constructor, as the mapping does).</summary>
internal static class StoredBuckets
{
    public static Bucket Of(Guid id, string name, Currency currency, long version, DateTimeOffset createdAt, BucketStatus status) =>
        Of(id, name, currency, createdAt, status, BucketKind.Savings, null, BucketSystemRole.None, version);

    public static Bucket Of(
        Guid id,
        string? name,
        Currency currency,
        DateTimeOffset createdAt,
        BucketStatus status,
        BucketKind kind,
        InvestmentProfile? profile,
        BucketSystemRole role,
        long version = 1)
    {
        var constructor = typeof(Bucket).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        return (Bucket)constructor.Invoke(
            [id, FakeOwnerContext.Owner, Bucket.CurrentSchemaVersion, version, createdAt, createdAt, name, currency, kind, profile, status, role]);
    }
}

/// <summary>Builds settings as the database returns them (through the private constructor, as the mapping does).</summary>
internal static class StoredSettings
{
    public static readonly Guid Id = Guid.Parse("0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b");

    public static OwnerSettings Of(Currency currency, string timeZone, string locale, long version = 2)
    {
        var constructor = typeof(OwnerSettings).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        return (OwnerSettings)constructor.Invoke(
            [Id, FakeOwnerContext.Owner, OwnerSettings.CurrentSchemaVersion, version, FakeClock.DefaultStart, FakeClock.DefaultStart, currency, timeZone, locale]);
    }
}

/// <summary>
/// Exchange rates from a fixed table: a pair that is not in it fails with <c>fx.rate_unavailable</c>, as
/// the real provider does when nothing is stored. Records every request. Safe for CsCheck's threads.
/// </summary>
internal sealed class FakeFxRateProvider : IFxRateProvider
{
    private readonly Dictionary<(Currency From, Currency To), FxRate> rates = [];
    private readonly List<(Currency From, Currency To)> requests = [];

    public IReadOnlyList<(Currency From, Currency To)> Requests
    {
        get
        {
            lock (requests)
            {
                return [.. requests];
            }
        }
    }

    public FakeFxRateProvider With(Currency from, Currency to, decimal rate, DateOnly date)
    {
        rates[(from, to)] = new FxRate(from, to, rate, date);
        return this;
    }

    public Task<Result<FxRate>> GetRateAsync(Currency from, Currency to, CancellationToken cancellationToken)
    {
        lock (requests)
        {
            requests.Add((from, to));
        }

        return Task.FromResult(rates.TryGetValue((from, to), out var rate)
            ? Result.Ok(rate)
            : Result.Fail<FxRate>(AppError.Unavailable(ErrorCodes.FxRateUnavailable)));
    }
}
