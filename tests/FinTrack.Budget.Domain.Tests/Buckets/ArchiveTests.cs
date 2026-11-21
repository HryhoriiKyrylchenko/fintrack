using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Domain.Tests.Buckets;

/// <summary>
/// Archiving a bucket (ARCHITECTURE 4.1, invariant 8, plan 1b B17): only a bucket of the user that lends to no open
/// loan and whose <c>NetContributed</c> is zero, never a system bucket; checked in that order.
/// </summary>
public class ArchiveTests
{
    private static readonly OwnerId Owner = OwnerId.From(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));

    [Fact]
    public void EmptyBucket_IsArchived_KeepingIdentityAndVersion()
    {
        var bucket = StoredBuckets.Restore(BucketId.From(Guid.CreateVersion7()), Owner, "Cushion", Currency.Pln, BucketKind.Savings, null, BucketStatus.Active, BucketSystemRole.None, version: 4);

        var archived = bucket.Archive(Balance(bucket, 0m), lendsToAnOpenLoan: false).Value;

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

        bucket.Archive(Balance(bucket, (decimal)available), lendsToAnOpenLoan: false).ShouldFailWith(BudgetErrorCodes.BucketNotEmpty, ErrorKind.BusinessRule);
    }

    [Fact]
    public void ALenderOfAnOpenLoan_HasAnOpenLoan_BeforeItIsNotEmpty()
    {
        var bucket = Bucket.Create(BucketId.From(Guid.CreateVersion7()), Owner, "Cushion", Currency.Pln, BucketKind.Savings, null, []).Value;

        bucket.Archive(Balance(bucket, 0m), lendsToAnOpenLoan: true).ShouldFailWith(BudgetErrorCodes.BucketHasOpenLoan, ErrorKind.BusinessRule);
        bucket.Archive(Balance(bucket, 50m), lendsToAnOpenLoan: true).ShouldFailWith(BudgetErrorCodes.BucketHasOpenLoan, ErrorKind.BusinessRule);
    }

    [Fact]
    public void ALenderWhoseMoneyIsAllLentOut_IsNotEmpty_ThoughItsAvailableIsZero()
    {
        var bucket = Bucket.Create(BucketId.From(Guid.CreateVersion7()), Owner, "Cushion", Currency.Pln, BucketKind.Savings, null, []).Value;
        var balance = Balance(bucket, 100m) with { OutstandingPrincipal = Money.From(100m, Currency.Pln) };
        balance.Available.ShouldBe(Money.Zero(Currency.Pln));

        bucket.Archive(balance, lendsToAnOpenLoan: false).ShouldFailWith(BudgetErrorCodes.BucketNotEmpty, ErrorKind.BusinessRule);
    }

    [Fact]
    public void SystemBucket_IsNeverArchived()
    {
        var unallocated = Bucket.Unallocated(BucketId.From(Guid.CreateVersion7()), Owner, Currency.Pln);

        unallocated.Archive(Balance(unallocated, 0m), lendsToAnOpenLoan: true).ShouldFailWith(BudgetErrorCodes.BucketSystemBucket, ErrorKind.BusinessRule);
    }

    [Fact]
    public void BalanceOfAnotherBucket_IsABug()
    {
        var bucket = Bucket.Create(BucketId.From(Guid.CreateVersion7()), Owner, "Cushion", Currency.Pln, BucketKind.Savings, null, []).Value;
        var other = Bucket.Create(BucketId.From(Guid.CreateVersion7()), Owner, "Holiday", Currency.Pln, BucketKind.Savings, null, []).Value;

        Should.Throw<ArgumentException>(() => bucket.Archive(Balance(other, 0m), lendsToAnOpenLoan: false));
    }

    private static BucketBalance Balance(Bucket bucket, decimal available) =>
        BucketBalance.Empty(bucket) with { NetContributed = Money.From(available, bucket.Currency) };
}
