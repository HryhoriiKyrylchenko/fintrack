using FinTrack.Shared.Infrastructure.Fx;
using FinTrack.Shared.Infrastructure.Mongo;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Persistence;
using FinTrack.Tests.Common;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.IntegrationTests.Mongo;

/// <summary>
/// Reference data shared by all owners (plan 1a D8): only in <c>ref_</c> collections, only through
/// <see cref="ReferenceCollection{TDocument}"/>, and never owner data.
/// </summary>
public sealed class ReferenceCollectionTests(MongoDatabaseFixture fixture)
{
    private static readonly DateOnly Friday = new(2026, 1, 16);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void AReferenceCollection_NeedsTheRefPrefix() =>
        Should.Throw<ArgumentException>(() => new ReferenceCollection<FxRateSet>(fixture.Database, "fx_rates"));

    [Fact]
    public void OwnerDocuments_CannotBeReferenceData() =>
        Should.Throw<InvalidOperationException>(() => new ReferenceCollection<OwnedReference>(fixture.Database, "ref_owned"));

    [Fact]
    public void OwnerData_CannotBeStoredInARefCollection() =>
        Should.Throw<ArgumentException>(() => new AppendOnlyCollection<TestOwnerEntry>(fixture.Database, "ref_entries", new FixedOwner()));

    [Fact]
    public async Task Upsert_InsertsOnce_ThenReplacesTheFields_KeepingTheId()
    {
        var (rates, raw) = await NewRatesAsync();
        var first = Set(Friday, 4.2m, FakeClock.DefaultStart);
        var again = Set(Friday, 4.3m, FakeClock.DefaultStart.AddDays(1));

        await rates.UpsertAsync(KeyOf(first), first, Cancellation);
        await rates.UpsertAsync(KeyOf(again), again, Cancellation);

        var stored = (await raw.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation)).ShouldHaveSingleItem();
        stored["_id"].AsGuid.ShouldBe(first.Id);
        stored["rates"]["PLN"].ShouldBe(new BsonDecimal128(4.3m));
        stored["fetchedAt"].ToUniversalTime().ShouldBe(again.FetchedAt.UtcDateTime);
    }

    [Fact]
    public async Task StoredShape_IsExplicit()
    {
        var (rates, raw) = await NewRatesAsync();
        var set = Set(Friday, 4.2345m, FakeClock.DefaultStart);

        await rates.UpsertAsync(KeyOf(set), set, Cancellation);

        // An upsert stores the filter's fields first, then the rest; the order has no meaning, so compare by name.
        var stored = (await raw.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation)).ShouldHaveSingleItem();
        ByName(stored).ShouldBe(ByName(new BsonDocument
        {
            { "_id", new BsonBinaryData(set.Id, GuidRepresentation.Standard) },
            { "schemaVersion", 1 },
            { "base", "EUR" },
            { "date", "2026-01-16" },
            { "rates", new BsonDocument { { "PLN", new BsonDecimal128(4.2345m) }, { "USD", new BsonDecimal128(1.1m) } } },
            { "fetchedAt", new BsonDateTime(FakeClock.DefaultStart.UtcDateTime) },
        }));
        (await rates.FindFirstAsync(new BsonDocument("base", "EUR"), new BsonDocument("date", -1), Cancellation)).ShouldBeEquivalentTo(set);
    }

    [Fact]
    public async Task Reading_RejectsARateThatIsNotDecimal128()
    {
        var (rates, raw) = await NewRatesAsync();
        var set = Set(Friday, 4.2m, FakeClock.DefaultStart);
        await rates.UpsertAsync(KeyOf(set), set, Cancellation);
        await raw.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty, new BsonDocument("$set", new BsonDocument("rates.PLN", 4.2)), cancellationToken: Cancellation);

        await Should.ThrowAsync<FormatException>(() => rates.FindFirstAsync(new BsonDocument("base", "EUR"), new BsonDocument("date", -1), Cancellation));
    }

    [Fact]
    public async Task TheIndex_AllowsOneSetPerBaseAndDate()
    {
        var (_, raw) = await NewRatesAsync();
        var document = new BsonDocument { { "_id", new BsonBinaryData(Guid.CreateVersion7(), GuidRepresentation.Standard) }, { "base", "EUR" }, { "date", "2026-01-16" } };
        await raw.InsertOneAsync(document, cancellationToken: Cancellation);

        var duplicate = await Should.ThrowAsync<MongoWriteException>(() => raw.InsertOneAsync(
            new BsonDocument { { "_id", new BsonBinaryData(Guid.CreateVersion7(), GuidRepresentation.Standard) }, { "base", "EUR" }, { "date", "2026-01-16" } }, cancellationToken: Cancellation));

        duplicate.WriteError.Category.ShouldBe(ServerErrorCategory.DuplicateKey);
        (await raw.Indexes.List(Cancellation).ToListAsync(Cancellation)).ShouldContain(index => index["name"] == "base_date_unique" && index["unique"] == true);
    }

    private async Task<(ReferenceCollection<FxRateSet> Rates, IMongoCollection<BsonDocument> Raw)> NewRatesAsync()
    {
        var name = $"ref_it_{Guid.NewGuid():N}";
        await FxRateIndexInitializer.CreateIndexAsync(fixture.Database, name, Cancellation);
        return (new ReferenceCollection<FxRateSet>(fixture.Database, name), fixture.Database.GetCollection<BsonDocument>(name));
    }

    private static BsonDocument ByName(BsonDocument document) => new(document.OrderBy(element => element.Name, StringComparer.Ordinal));

    private static BsonDocument KeyOf(FxRateSet set) => new() { { "base", set.Base.Code }, { "date", set.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) } };

    private static FxRateSet Set(DateOnly date, decimal pln, DateTimeOffset fetchedAt) =>
        new(Guid.CreateVersion7(fetchedAt), FxRateSet.CurrentSchemaVersion, Currency.Eur, date, new Dictionary<string, decimal> { ["PLN"] = pln, ["USD"] = 1.1m }, fetchedAt);

    // A type that is both: the collection must refuse it.
    private sealed class OwnedReference : IReferenceDocument, IDocument
    {
        public Guid Id { get; } = Guid.CreateVersion7();

        public OwnerId OwnerId { get; } = OwnerId.From(Guid.CreateVersion7());

        public int SchemaVersion => 1;

        public static void RegisterMapping()
        {
        }
    }

    private sealed class TestOwnerEntry : IAppendOnlyDocument
    {
        public Guid Id { get; } = Guid.CreateVersion7();

        public OwnerId OwnerId { get; } = OwnerId.From(Guid.CreateVersion7());

        public int SchemaVersion => 1;

        public DateTimeOffset RecordedAt { get; } = FakeClock.DefaultStart;
    }

    private sealed class FixedOwner : IOwnerContext
    {
        public OwnerId OwnerId { get; } = OwnerId.From(Guid.CreateVersion7());
    }
}
