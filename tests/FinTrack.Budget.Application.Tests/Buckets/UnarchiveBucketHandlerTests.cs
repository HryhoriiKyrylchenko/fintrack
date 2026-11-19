using FinTrack.Budget.Application.Buckets;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Application.Tests.Buckets;

/// <summary>
/// <c>UnarchiveBucket</c> (plan 1b B23) against in-memory fakes: an archived bucket becomes active again under the
/// version it was read with, an active one writes nothing, and a name taken in the meantime is refused.
/// </summary>
public class UnarchiveBucketHandlerTests
{
    private readonly CallLog log = new();
    private readonly FakeLedger ledger;
    private readonly FakeBudgetStore store;

    public UnarchiveBucketHandlerTests()
    {
        ledger = new FakeLedger(log);
        store = new FakeBudgetStore(log) { Settings = StoredSettings.Of(Currency.Pln, "Europe/Warsaw", "pl-PL") };
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ArchivedBucket_IsSavedActive_UnderTheVersionItWasReadWith()
    {
        var archived = StoredBuckets.Of(Guid.CreateVersion7(), "Holiday", Currency.Pln, 3, FakeClock.DefaultStart, BucketStatus.Archived);
        store.Buckets.Add(archived);
        ledger.Version = 7;

        (await Unarchive(archived.Id)).IsSuccess.ShouldBeTrue();

        var commit = ledger.Commits.ShouldHaveSingleItem();
        commit.Appends.ShouldBeEmpty();
        var saved = commit.Saves.ShouldHaveSingleItem().ShouldBeOfType<Bucket>();
        saved.Id.ShouldBe(archived.Id);
        saved.Status.ShouldBe(BucketStatus.Active);
        saved.Version.ShouldBe(3);
        commit.ExpectedVersion.ShouldBe(7);
    }

    [Fact]
    public async Task ActiveBucket_UnarchivedAgain_WritesNothing()
    {
        var active = StoredBuckets.Of(Guid.CreateVersion7(), "Cushion", Currency.Pln, 1, FakeClock.DefaultStart, BucketStatus.Active);
        store.Buckets.Add(active);

        (await Unarchive(active.Id)).IsSuccess.ShouldBeTrue();
        ledger.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task NameTakenByAnActiveBucket_IsRejected_AndWritesNothing()
    {
        var archived = StoredBuckets.Of(Guid.CreateVersion7(), "Holiday", Currency.Pln, 2, FakeClock.DefaultStart, BucketStatus.Archived);
        store.Buckets.Add(archived);
        store.Buckets.Add(StoredBuckets.Of(Guid.CreateVersion7(), "holiday", Currency.Eur, 1, FakeClock.DefaultStart.AddSeconds(1), BucketStatus.Active));

        (await Unarchive(archived.Id)).ShouldFailWith(BudgetErrorCodes.BucketNameTaken, ErrorKind.BusinessRule);
        ledger.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task SystemBucket_AndUnknownBucket_AreRejected()
    {
        var unallocated = Bucket.Unallocated(BucketId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, Currency.Eur);
        store.Buckets.Add(unallocated);

        (await Unarchive(unallocated.Id)).ShouldFailWith(BudgetErrorCodes.BucketSystemBucket, ErrorKind.BusinessRule);
        (await Unarchive(Guid.CreateVersion7())).ShouldFailWith(BudgetErrorCodes.BucketNotFound, ErrorKind.NotFound);
        ledger.Commits.ShouldBeEmpty();
    }

    private Task<FluentResults.Result> Unarchive(Guid bucket) =>
        new UnarchiveBucketHandler(new BudgetStateLoader(ledger, store), ledger).Handle(new UnarchiveBucket(bucket), Cancellation).AsTask();
}
