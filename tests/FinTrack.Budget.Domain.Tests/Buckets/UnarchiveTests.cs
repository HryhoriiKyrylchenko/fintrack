using FinTrack.Budget.Domain.Buckets;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Domain.Tests.Buckets;

/// <summary>
/// Unarchiving a bucket (ARCHITECTURE 4.1, plan 1b B23): an archived bucket of the user becomes active again
/// with its identity and history, unless an active bucket has taken its name in the meantime.
/// </summary>
public class UnarchiveTests
{
    private static readonly OwnerId Owner = OwnerId.From(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));

    [Fact]
    public void ArchivedBucket_IsActiveAgain_KeepingIdentityNameAndVersion()
    {
        var bucket = Archived("Holiday", version: 5);

        var active = bucket.Unarchive([bucket]).Value;

        active.Status.ShouldBe(BucketStatus.Active);
        active.Id.ShouldBe(bucket.Id);
        active.Version.ShouldBe(5);
        active.Name.ShouldBe("Holiday");
        active.Currency.ShouldBe(Currency.Pln);
        active.Kind.ShouldBe(BucketKind.Savings);
        bucket.Status.ShouldBe(BucketStatus.Archived);
    }

    [Fact]
    public void ActiveBucket_IsReturnedUnchanged()
    {
        var bucket = StoredBuckets.Restore(BucketId.From(Guid.CreateVersion7()), Owner, "Cushion", Currency.Pln, BucketKind.Savings, null, BucketStatus.Active, BucketSystemRole.None, version: 2);

        bucket.Unarchive([bucket]).Value.ShouldBeSameAs(bucket);
    }

    [Theory]
    [InlineData("Holiday")]
    [InlineData("HOLIDAY")]
    public void NameTakenByAnActiveBucket_IsRejected(string otherName)
    {
        var bucket = Archived("Holiday", version: 1);
        var other = StoredBuckets.Restore(BucketId.From(Guid.CreateVersion7()), Owner, otherName, Currency.Eur, BucketKind.Savings, null, BucketStatus.Active, BucketSystemRole.None, version: 1);

        bucket.Unarchive([bucket, other]).ShouldFailWith(BudgetErrorCodes.BucketNameTaken, ErrorKind.BusinessRule);
    }

    [Fact]
    public void NameUsedOnlyByAnotherArchivedBucket_IsFree()
    {
        var bucket = Archived("Holiday", version: 1);
        var older = Archived("Holiday", version: 3);

        bucket.Unarchive([bucket, older]).Value.Status.ShouldBe(BucketStatus.Active);
    }

    [Fact]
    public void SystemBucket_IsRejected()
    {
        var unallocated = Bucket.Unallocated(BucketId.From(Guid.CreateVersion7()), Owner, Currency.Pln);

        unallocated.Unarchive([unallocated]).ShouldFailWith(BudgetErrorCodes.BucketSystemBucket, ErrorKind.BusinessRule);
    }

    private static Bucket Archived(string name, long version) =>
        StoredBuckets.Restore(BucketId.From(Guid.CreateVersion7()), Owner, name, Currency.Pln, BucketKind.Savings, null, BucketStatus.Archived, BucketSystemRole.None, version);
}
