using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Domain.Tests.Buckets;

/// <summary>
/// Archiving a bucket (ARCHITECTURE 4.1): only an empty bucket of the user, never a system bucket. In
/// phase 1a "empty" is an <c>Available</c> of zero (no loans or plans exist yet).
/// </summary>
public class ArchiveTests
{
    private static readonly OwnerId Owner = OwnerId.From(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));

    [Fact]
    public void EmptyBucket_IsArchived_KeepingIdentityAndVersion()
    {
        var bucket = StoredBuckets.Restore(BucketId.From(Guid.CreateVersion7()), Owner, "Cushion", Currency.Pln, BucketKind.Savings, null, BucketStatus.Active, BucketSystemRole.None, version: 4);

        var archived = bucket.Archive(Balance(bucket, 0m)).Value;

        archived.Status.ShouldBe(BucketStatus.Archived);
        archived.Id.ShouldBe(bucket.Id);
        archived.Version.ShouldBe(4);
        archived.Name.ShouldBe("Cushion");
        bucket.Status.ShouldBe(BucketStatus.Active);
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(-5)]
    public void BucketWithABalance_IsNotEmpty(double available)
    {
        var bucket = Bucket.Create(BucketId.From(Guid.CreateVersion7()), Owner, "Stocks", Currency.Pln, BucketKind.Investment, InvestmentProfile.Crypto, []).Value;

        bucket.Archive(Balance(bucket, (decimal)available)).ShouldFailWith(BudgetErrorCodes.BucketNotEmpty, ErrorKind.BusinessRule);
    }

    [Fact]
    public void SystemBucket_IsNeverArchived()
    {
        var unallocated = Bucket.Unallocated(BucketId.From(Guid.CreateVersion7()), Owner, Currency.Pln);

        unallocated.Archive(Balance(unallocated, 0m)).ShouldFailWith(BudgetErrorCodes.BucketSystemBucket, ErrorKind.BusinessRule);
    }

    [Fact]
    public void BalanceOfAnotherBucket_IsABug()
    {
        var bucket = Bucket.Create(BucketId.From(Guid.CreateVersion7()), Owner, "Cushion", Currency.Pln, BucketKind.Savings, null, []).Value;
        var other = Bucket.Create(BucketId.From(Guid.CreateVersion7()), Owner, "Holiday", Currency.Pln, BucketKind.Savings, null, []).Value;

        Should.Throw<ArgumentException>(() => bucket.Archive(Balance(other, 0m)));
    }

    private static BucketBalance Balance(Bucket bucket, decimal available) =>
        BucketBalance.Empty(bucket) with { NetContributed = Money.From(available, bucket.Currency) };
}
