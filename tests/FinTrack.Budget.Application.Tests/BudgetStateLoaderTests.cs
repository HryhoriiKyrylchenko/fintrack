using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Application.Tests;

/// <summary>
/// The loader returns documents from one point in time. A commit that lands between its reads (the
/// journal could then mention a bucket the bucket list does not have) is detected by the version, which
/// every commit raises: the loader reads again, and reports a conflict when the state keeps changing.
/// </summary>
public class BudgetStateLoaderTests
{
    private readonly CallLog log = new();
    private readonly FakeLedger ledger;
    private readonly FakeBudgetStore store;

    public BudgetStateLoaderTests()
    {
        ledger = new FakeLedger(log) { Version = 4 };
        store = new FakeBudgetStore(log);
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UnchangedVersion_GivesTheSnapshot_FromOneRead()
    {
        var snapshot = (await new BudgetStateLoader(ledger, store).LoadAsync(Cancellation)).Value;

        snapshot.Version.ShouldBe(4);
        log.Calls.ShouldBe(["version", "settings", "buckets", "journal", "version"]);
    }

    [Fact]
    public async Task CommitBetweenTheReads_IsDetected_AndTheStateIsReadAgain()
    {
        var bucket = Bucket.Unallocated(BucketId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, Currency.Pln);
        var deposit = Deposit.Create(JournalEntryId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, bucket.BucketId, Money.From(1m, Currency.Pln),
            new DateOnly(2026, 1, 15), new DateOnly(2026, 1, 15), EntryInfo.None, FakeClock.DefaultStart).Value;

        // The first time, another request commits the bucket and its deposit after the buckets were read:
        // this read's journal mentions a bucket its bucket list lacks.
        var landed = false;
        store.AfterJournalRead = () =>
        {
            if (!landed)
            {
                landed = true;
                store.Buckets.Add(bucket);
                store.Journal.Add(deposit);
                ledger.Version++;
            }
        };
        store.Journal.Add(deposit);

        var snapshot = (await new BudgetStateLoader(ledger, store).LoadAsync(Cancellation)).Value;

        snapshot.Version.ShouldBe(5);
        snapshot.Buckets.ShouldContain(bucket);
        snapshot.State.BalanceOf(bucket.BucketId).Available.ShouldBe(Money.From(2m, Currency.Pln));
        log.Calls.Count(call => call == "journal").ShouldBe(2);
    }

    [Fact]
    public async Task StateThatKeepsChanging_IsAConflict_AfterTheLastAttempt()
    {
        store.AfterJournalRead = () => ledger.Version++;

        var result = await new BudgetStateLoader(ledger, store).LoadAsync(Cancellation);

        result.ShouldFailWith(ErrorCodes.ConcurrencyConflict, ErrorKind.Conflict);
        log.Calls.Count(call => call == "journal").ShouldBe(BudgetStateLoader.MaxAttempts);
    }
}
