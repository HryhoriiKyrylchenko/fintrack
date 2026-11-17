using System.Reflection;
using FinTrack.Budget.Application;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace FinTrack.Budget.Infrastructure.IntegrationTests.Buckets;

/// <summary>
/// The stored format of <see cref="Bucket"/> in <c>budget_buckets</c>, element by element. It is
/// permanent (ARCHITECTURE 2.2). Fields that are optional by design are absent when empty, and a
/// document without them reads (contradiction 6 of the plan: a field added later needs no upcaster).
/// </summary>
public sealed class BucketStorageTests(BudgetFixture fixture)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SavingsBucket_HasThePinnedShape_WithoutProfile()
    {
        var owner = BudgetFixture.NewOwner();
        var bucket = Bucket.Create(NewId(), owner, "Cushion", Currency.Pln, BucketKind.Savings, null, []).Value;
        await CommitAsync(owner, bucket);

        (await RawAsync(owner, bucket.Id)).ShouldBe(new BsonDocument
        {
            { "_id", Uuid(bucket.Id) },
            { "ownerId", Uuid(owner.Value) },
            { "schemaVersion", 1 },
            { "version", 1L },
            { "createdAt", new BsonDateTime(FakeClock.DefaultStart.UtcDateTime) },
            { "updatedAt", new BsonDateTime(FakeClock.DefaultStart.UtcDateTime) },
            { "name", "Cushion" },
            { "currency", "PLN" },
            { "kind", "Savings" },
            { "status", "Active" },
            { "systemRole", "None" },
        });
    }

    [Fact]
    public async Task InvestmentBucket_HasItsProfile()
    {
        var owner = BudgetFixture.NewOwner();
        var bucket = Bucket.Create(NewId(), owner, "Crypto", Currency.Usd, BucketKind.Investment, InvestmentProfile.Crypto, []).Value;
        await CommitAsync(owner, bucket);

        var stored = await RawAsync(owner, bucket.Id);

        stored["kind"].ShouldBe(new BsonString("Investment"));
        stored["profile"].ShouldBe(new BsonString("Crypto"));
    }

    [Fact]
    public async Task UnallocatedBucket_HasNoNameAndNoProfile()
    {
        var owner = BudgetFixture.NewOwner();
        var bucket = Bucket.Unallocated(NewId(), owner, Currency.Eur);
        await CommitAsync(owner, bucket);

        var stored = await RawAsync(owner, bucket.Id);

        stored.Names.ShouldBe(["_id", "ownerId", "schemaVersion", "version", "createdAt", "updatedAt", "currency", "kind", "status", "systemRole"]);
        stored["systemRole"].ShouldBe(new BsonString("Unallocated"));
    }

    [Fact]
    public async Task StoredBuckets_ReadBackEqual()
    {
        var owner = BudgetFixture.NewOwner();
        var savings = Bucket.Create(NewId(), owner, "Cushion", Currency.Pln, BucketKind.Savings, null, []).Value;
        var investment = Bucket.Create(NewId(), owner, "Stocks", Currency.Usd, BucketKind.Investment, InvestmentProfile.RealValueAssets, []).Value;
        var unallocated = Bucket.Unallocated(NewId(), owner, Currency.Pln);
        await CommitAsync(owner, savings, investment, unallocated);

        var read = (await ReadAsync(owner)).ToDictionary(bucket => bucket.Id);

        read[savings.Id].HasDefinition("Cushion", Currency.Pln, BucketKind.Savings, null).ShouldBeTrue();
        read[investment.Id].HasDefinition("Stocks", Currency.Usd, BucketKind.Investment, InvestmentProfile.RealValueAssets).ShouldBeTrue();
        read[unallocated.Id].Name.ShouldBeNull();
        read[unallocated.Id].SystemRole.ShouldBe(BucketSystemRole.Unallocated);
        read.Values.ShouldAllBe(bucket => bucket.Version == 1 && bucket.Status == BucketStatus.Active && bucket.OwnerId == owner);
    }

    [Fact]
    public async Task DocumentWithoutAnOptionalElement_Reads_AsAnEmptyValue()
    {
        // As a document written before the field existed: no "profile", no "name".
        var owner = BudgetFixture.NewOwner();
        var document = ValidDocument(owner);
        document.Remove("name");
        await fixture.Raw(BudgetCollections.Buckets).InsertOneAsync(document, cancellationToken: Cancellation);

        var bucket = (await ReadAsync(owner)).ShouldHaveSingleItem();

        bucket.Profile.ShouldBeNull();
        bucket.Name.ShouldBeNull();
    }

    [Theory]
    [InlineData("an unknown element", "colour", "blue")]
    [InlineData("an unknown kind", "kind", "Pension")]
    [InlineData("a kind in another case", "kind", "savings")]
    [InlineData("a kind as a number", "kind", 0)]
    [InlineData("an unknown status", "status", "Deleted")]
    [InlineData("an unregistered currency", "currency", "XYZ")]
    public async Task StoredDocument_ThatDoesNotFitTheFormat_FailsToRead(string reason, string element, object value)
    {
        var owner = BudgetFixture.NewOwner();
        var document = ValidDocument(owner);
        document[element] = BsonValue.Create(value);
        await fixture.Raw(BudgetCollections.Buckets).InsertOneAsync(document, cancellationToken: Cancellation);

        await Should.ThrowAsync<FormatException>(() => ReadAsync(owner), reason);
    }

    [Theory]
    [InlineData("currency")]
    [InlineData("kind")]
    [InlineData("status")]
    [InlineData("systemRole")]
    public async Task StoredDocument_WithoutARequiredElement_FailsToRead(string element)
    {
        var owner = BudgetFixture.NewOwner();
        var document = ValidDocument(owner);
        document.Remove(element);
        await fixture.Raw(BudgetCollections.Buckets).InsertOneAsync(document, cancellationToken: Cancellation);

        await Should.ThrowAsync<BsonSerializationException>(() => ReadAsync(owner));
    }

    [Fact]
    public void Mapping_StoresEveryProperty_ExceptTheComputedId()
    {
        BudgetMongoMapping.Register();
        var mapped = BsonClassMap.LookupClassMap(typeof(Bucket)).AllMemberMaps.Select(member => member.MemberName);

        var properties = typeof(Bucket).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .Where(name => name != nameof(Bucket.BucketId));

        mapped.ShouldBe(properties, ignoreOrder: true);
    }

    [Fact]
    public async Task SecondUnallocatedOfTheSameCurrency_IsRejectedByTheIndex()
    {
        var owner = BudgetFixture.NewOwner();
        await fixture.Raw(BudgetCollections.Buckets).InsertOneAsync(UnallocatedDocument(owner, "PLN"), cancellationToken: Cancellation);

        var exception = await Should.ThrowAsync<MongoWriteException>(() =>
            fixture.Raw(BudgetCollections.Buckets).InsertOneAsync(UnallocatedDocument(owner, "PLN"), cancellationToken: Cancellation));
        exception.WriteError.Category.ShouldBe(ServerErrorCategory.DuplicateKey);
    }

    [Fact]
    public async Task TheIndex_AllowsOneUnallocatedPerCurrency_AndAnyNumberOfOwnBuckets()
    {
        var owner = BudgetFixture.NewOwner();
        var buckets = fixture.Raw(BudgetCollections.Buckets);

        await buckets.InsertOneAsync(UnallocatedDocument(owner, "PLN"), cancellationToken: Cancellation);
        await buckets.InsertOneAsync(UnallocatedDocument(owner, "EUR"), cancellationToken: Cancellation);
        await buckets.InsertOneAsync(UnallocatedDocument(BudgetFixture.NewOwner(), "PLN"), cancellationToken: Cancellation);
        await buckets.InsertOneAsync(ValidDocument(owner), cancellationToken: Cancellation);
        await buckets.InsertOneAsync(ValidDocument(owner), cancellationToken: Cancellation);

        (await ReadAsync(owner)).Count.ShouldBe(4);
    }

    [Fact]
    public async Task UnallocatedIndex_IsPartialOnTheStoredElements()
    {
        using var cursor = await fixture.Raw(BudgetCollections.Buckets).Indexes.ListAsync(Cancellation);
        var index = (await cursor.ToListAsync(Cancellation)).Single(candidate => candidate["name"] == "unallocated_per_currency_unique");

        index["key"].AsBsonDocument.ShouldBe(new BsonDocument { { "ownerId", 1 }, { "systemRole", 1 }, { "currency", 1 } });
        index["unique"].AsBoolean.ShouldBeTrue();
        index["partialFilterExpression"].AsBsonDocument.ShouldBe(new BsonDocument("systemRole", "Unallocated"));
    }

    private async Task CommitAsync(OwnerId owner, params Bucket[] buckets)
    {
        await using var scope = fixture.ScopeFor(owner);
        var ledger = scope.ServiceProvider.GetRequiredService<ILedger>();
        var version = await ledger.GetVersionAsync(BudgetLedger.Name, Cancellation);
        (await ledger.CommitAsync(new LedgerCommit(BudgetLedger.Name, version, saves: buckets), Cancellation)).IsSuccess.ShouldBeTrue();
    }

    private async Task<IReadOnlyList<Bucket>> ReadAsync(OwnerId owner)
    {
        await using var scope = fixture.ScopeFor(owner);
        return await scope.ServiceProvider.GetRequiredService<IBudgetStore>().GetBucketsAsync(Cancellation);
    }

    private async Task<BsonDocument> RawAsync(OwnerId owner, Guid id) =>
        await fixture.Raw(BudgetCollections.Buckets)
            .Find(new BsonDocument { { "_id", Uuid(id) }, { "ownerId", Uuid(owner.Value) } })
            .SingleAsync(Cancellation);

    private static BucketId NewId() => BucketId.From(Guid.CreateVersion7());

    private static BsonBinaryData Uuid(Guid value) => new(value, GuidRepresentation.Standard);

    private static BsonDocument ValidDocument(OwnerId owner) => new()
    {
        { "_id", Uuid(Guid.CreateVersion7()) },
        { "ownerId", Uuid(owner.Value) },
        { "schemaVersion", 1 },
        { "version", 1L },
        { "createdAt", new BsonDateTime(FakeClock.DefaultStart.UtcDateTime) },
        { "updatedAt", new BsonDateTime(FakeClock.DefaultStart.UtcDateTime) },
        { "name", "Cushion" },
        { "currency", "PLN" },
        { "kind", "Savings" },
        { "status", "Active" },
        { "systemRole", "None" },
    };

    private static BsonDocument UnallocatedDocument(OwnerId owner, string currency)
    {
        var document = ValidDocument(owner);
        document.Remove("name");
        document["currency"] = currency;
        document["systemRole"] = "Unallocated";
        return document;
    }
}
