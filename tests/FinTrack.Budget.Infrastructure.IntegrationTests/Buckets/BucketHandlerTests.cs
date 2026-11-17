using FinTrack.Budget.Application;
using FinTrack.Budget.Application.Buckets;
using FinTrack.Budget.Contracts.V1.Buckets;
using FinTrack.Budget.Domain;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;
using FluentResults;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FinTrack.Budget.Infrastructure.IntegrationTests.Buckets;

/// <summary>Creating, renaming and listing buckets through the real ledger and MongoDB, owner by owner.</summary>
public sealed class BucketHandlerTests(BudgetFixture fixture)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreatedBuckets_AreListed()
    {
        var owner = BudgetFixture.NewOwner();

        // The fake clock gives both the same creation time, so the order comes from the IDs: version 7
        // IDs sort by their time part only when it differs (within one millisecond the rest is random).
        var now = DateTimeOffset.UtcNow;
        var cushion = Guid.CreateVersion7(now);
        var crypto = Guid.CreateVersion7(now.AddMilliseconds(1));

        (await CreateAsync(owner, new CreateBucket(cushion, "Cushion", Currency.Pln, BucketKind.Savings, null))).IsSuccess.ShouldBeTrue();
        (await CreateAsync(owner, new CreateBucket(crypto, "Crypto", Currency.Usd, BucketKind.Investment, InvestmentProfile.Crypto))).IsSuccess.ShouldBeTrue();

        (await ListAsync(owner)).ShouldBe(
        [
            new BucketDto(cushion, "Cushion", Currency.Pln, BucketKind.Savings, null, BucketStatus.Active, BucketSystemRole.None),
            new BucketDto(crypto, "Crypto", Currency.Usd, BucketKind.Investment, InvestmentProfile.Crypto, BucketStatus.Active, BucketSystemRole.None),
        ]);
        (await LedgerVersionAsync(owner)).ShouldBe(2);
    }

    [Fact]
    public async Task RepeatedCreate_WritesNothing()
    {
        var owner = BudgetFixture.NewOwner();
        var command = new CreateBucket(Guid.CreateVersion7(), "Cushion", Currency.Pln, BucketKind.Savings, null);
        await CreateAsync(owner, command);

        (await CreateAsync(owner, command)).IsSuccess.ShouldBeTrue();

        (await ListAsync(owner)).ShouldHaveSingleItem();
        (await LedgerVersionAsync(owner)).ShouldBe(1);
    }

    [Fact]
    public async Task RepeatedCreate_WithOtherValues_IsIdReused_AndWritesNothing()
    {
        var owner = BudgetFixture.NewOwner();
        var id = Guid.CreateVersion7();
        await CreateAsync(owner, new CreateBucket(id, "Cushion", Currency.Pln, BucketKind.Savings, null));

        (await CreateAsync(owner, new CreateBucket(id, "Cushion", Currency.Eur, BucketKind.Savings, null)))
            .ShouldFailWith(BudgetErrorCodes.BucketIdReused, ErrorKind.Conflict);

        (await ListAsync(owner)).ShouldHaveSingleItem().Currency.ShouldBe(Currency.Pln);
        (await LedgerVersionAsync(owner)).ShouldBe(1);
    }

    [Fact]
    public async Task TakenName_IsRejected_InAnyCase()
    {
        var owner = BudgetFixture.NewOwner();
        await CreateAsync(owner, new CreateBucket(Guid.CreateVersion7(), "Cushion", Currency.Pln, BucketKind.Savings, null));

        (await CreateAsync(owner, new CreateBucket(Guid.CreateVersion7(), "cUSHION", Currency.Eur, BucketKind.Savings, null)))
            .ShouldFailWith(BudgetErrorCodes.BucketNameTaken, ErrorKind.BusinessRule);
    }

    [Fact]
    public async Task Rename_ReplacesTheBucket_UnderTheNextVersion()
    {
        var owner = BudgetFixture.NewOwner();
        var id = Guid.CreateVersion7();
        await CreateAsync(owner, new CreateBucket(id, "Cushion", Currency.Pln, BucketKind.Savings, null));

        (await RenameAsync(owner, new RenameBucket(id, "Safety net"))).IsSuccess.ShouldBeTrue();

        (await ListAsync(owner)).ShouldHaveSingleItem().Name.ShouldBe("Safety net");
        (await StoredVersionAsync(owner, id)).ShouldBe(2);
        (await LedgerVersionAsync(owner)).ShouldBe(2);
    }

    [Fact]
    public async Task Owners_NeverSeeOrChangeEachOthersBuckets()
    {
        var first = BudgetFixture.NewOwner();
        var second = BudgetFixture.NewOwner();
        var id = Guid.CreateVersion7();
        await CreateAsync(first, new CreateBucket(id, "Cushion", Currency.Pln, BucketKind.Savings, null));

        (await ListAsync(second)).ShouldBeEmpty();
        (await RenameAsync(second, new RenameBucket(id, "Mine"))).ShouldFailWith(BudgetErrorCodes.BucketNotFound, ErrorKind.NotFound);

        // The same name is free for another owner.
        (await CreateAsync(second, new CreateBucket(Guid.CreateVersion7(), "Cushion", Currency.Pln, BucketKind.Savings, null))).IsSuccess.ShouldBeTrue();
        (await ListAsync(first)).ShouldHaveSingleItem().Name.ShouldBe("Cushion");
    }

    [Fact]
    public async Task ArchivedBuckets_AreListedOnlyOnRequest_AndFreeTheirName()
    {
        var owner = BudgetFixture.NewOwner();
        var id = Guid.CreateVersion7();
        await CreateAsync(owner, new CreateBucket(id, "Old", Currency.Pln, BucketKind.Savings, null));

        // Archiving arrives in step 9; until then the stored status is set directly.
        await fixture.Raw(BudgetCollections.Buckets).UpdateOneAsync(
            new BsonDocument("_id", new BsonBinaryData(id, GuidRepresentation.Standard)),
            new BsonDocument("$set", new BsonDocument("status", "Archived")),
            cancellationToken: Cancellation);

        (await ListAsync(owner)).ShouldBeEmpty();
        (await ListAsync(owner, includeArchived: true)).ShouldHaveSingleItem().Status.ShouldBe(BucketStatus.Archived);
        (await CreateAsync(owner, new CreateBucket(Guid.CreateVersion7(), "Old", Currency.Pln, BucketKind.Savings, null))).IsSuccess.ShouldBeTrue();
        (await RenameAsync(owner, new RenameBucket(id, "Older"))).ShouldFailWith(BudgetErrorCodes.BucketArchived, ErrorKind.BusinessRule);
    }

    private async Task<Result> CreateAsync(OwnerId owner, CreateBucket command)
    {
        await using var scope = fixture.ScopeFor(owner);
        return await ActivatorUtilities.CreateInstance<CreateBucketHandler>(scope.ServiceProvider).Handle(command, Cancellation);
    }

    private async Task<Result> RenameAsync(OwnerId owner, RenameBucket command)
    {
        await using var scope = fixture.ScopeFor(owner);
        return await ActivatorUtilities.CreateInstance<RenameBucketHandler>(scope.ServiceProvider).Handle(command, Cancellation);
    }

    private async Task<IReadOnlyList<BucketDto>> ListAsync(OwnerId owner, bool includeArchived = false)
    {
        await using var scope = fixture.ScopeFor(owner);
        return (await ActivatorUtilities.CreateInstance<GetBucketsHandler>(scope.ServiceProvider).Handle(new GetBuckets(includeArchived), Cancellation)).Value;
    }

    private async Task<long> LedgerVersionAsync(OwnerId owner)
    {
        await using var scope = fixture.ScopeFor(owner);
        return await scope.ServiceProvider.GetRequiredService<ILedger>().GetVersionAsync(BudgetLedger.Name, Cancellation);
    }

    private async Task<long> StoredVersionAsync(OwnerId owner, Guid id)
    {
        await using var scope = fixture.ScopeFor(owner);
        var buckets = await scope.ServiceProvider.GetRequiredService<IBudgetStore>().GetBucketsAsync(Cancellation);
        return buckets.Single(bucket => bucket.Id == id).Version;
    }
}
