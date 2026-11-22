using System.Globalization;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Time;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.IntegrationTests.Mongo;

/// <summary>
/// The stored format is permanent (ARCHITECTURE 2.2), so these tests read the raw BSON the
/// server holds, not only the typed round trip.
/// </summary>
public sealed class StorageFormatTests(MongoDatabaseFixture fixture)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Document_IsStoredInTheAgreedFormat()
    {
        var (typed, raw) = fixture.NewCollection<SampleDocument>();
        var document = SampleDocument.Create();

        await typed.InsertOneAsync(document, cancellationToken: Cancellation);
        var stored = await raw.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(Cancellation);

        stored.Names.ShouldBe(
        [
            "_id", "ownerId", "schemaVersion", "version", "createdAt", "updatedAt", "kind", "optionalKind",
            "occurredOn", "balance", "currency", "rate", "optionalRate", "parentId", "month", "optionalMonth",
        ]);

        ShouldBeStandardUuid(stored["_id"], document.Id);
        ShouldBeStandardUuid(stored["ownerId"], document.OwnerId.Value);
        ShouldBeStandardUuid(stored["parentId"], document.ParentId!.Value.Value);

        stored["schemaVersion"].ShouldBe(new BsonInt32(1));
        stored["version"].ShouldBe(new BsonInt64(3));

        stored["createdAt"].BsonType.ShouldBe(BsonType.DateTime);
        stored["createdAt"].ShouldBe(new BsonDateTime(document.CreatedAt.ToUnixTimeMilliseconds()));
        stored["updatedAt"].ShouldBe(new BsonDateTime(document.UpdatedAt.ToUnixTimeMilliseconds()));

        stored["kind"].ShouldBe(new BsonString("Investment"));
        stored["optionalKind"].ShouldBe(new BsonString("Savings"));
        stored["occurredOn"].ShouldBe(new BsonString("2026-01-15"));

        var balance = stored["balance"].AsBsonDocument;
        balance.Names.ShouldBe(["amount", "currency"]);
        balance["amount"].BsonType.ShouldBe(BsonType.Decimal128);
        balance["amount"].AsDecimal128.ShouldBe(new Decimal128(1234.56m));
        balance["currency"].ShouldBe(new BsonString("PLN"));

        stored["currency"].ShouldBe(new BsonString("EUR"));
        stored["rate"].BsonType.ShouldBe(BsonType.Decimal128);
        stored["rate"].AsDecimal128.ShouldBe(new Decimal128(4.3215m));
        stored["optionalRate"].AsDecimal128.ShouldBe(new Decimal128(0.000000000000000001m));

        stored["month"].ShouldBe(new BsonString("2026-01"));
        stored["optionalMonth"].ShouldBe(new BsonString("9999-12"));
    }

    [Theory]
    [InlineData(1, 1, "0001-01")]
    [InlineData(1, 12, "0001-12")]
    [InlineData(2026, 11, "2026-11")]
    [InlineData(9999, 1, "9999-01")]
    [InlineData(9999, 12, "9999-12")]
    public async Task Month_IsStoredAsYearDashMonthAndRoundTrips(int year, int month, string text)
    {
        var (typed, raw) = fixture.NewCollection<SampleDocument>();
        var document = SampleDocument.Create() with { Month = new YearMonth(year, month) };

        await typed.InsertOneAsync(document, cancellationToken: Cancellation);
        var stored = await raw.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(Cancellation);
        var read = await typed.Find(FilterDefinition<SampleDocument>.Empty).SingleAsync(Cancellation);

        stored["month"].ShouldBe(new BsonString(text));
        read.Month.ShouldBe(new YearMonth(year, month));
    }

    [Fact]
    public async Task Document_RoundTripsUnchanged()
    {
        var (typed, _) = fixture.NewCollection<SampleDocument>();
        var document = SampleDocument.Create();

        await typed.InsertOneAsync(document, cancellationToken: Cancellation);
        var read = await typed.Find(FilterDefinition<SampleDocument>.Empty).SingleAsync(Cancellation);

        read.ShouldBe(document);
    }

    [Fact]
    public async Task MissingOptionalValues_AreStoredAsNullAndRoundTrip()
    {
        var (typed, raw) = fixture.NewCollection<SampleDocument>();
        var document = SampleDocument.Create() with { OptionalKind = null, OptionalRate = null, ParentId = null, OptionalMonth = null };

        await typed.InsertOneAsync(document, cancellationToken: Cancellation);
        var stored = await raw.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(Cancellation);
        var read = await typed.Find(FilterDefinition<SampleDocument>.Empty).SingleAsync(Cancellation);

        stored["optionalMonth"].ShouldBe(BsonNull.Value);
        stored["optionalKind"].ShouldBe(BsonNull.Value);
        stored["optionalRate"].ShouldBe(BsonNull.Value);
        stored["parentId"].ShouldBe(BsonNull.Value);
        read.ShouldBe(document);
    }

    [Fact]
    public async Task Money_IsStoredAtItsCurrencysScale()
    {
        var (typed, raw) = fixture.NewCollection<SampleDocument>();

        await typed.InsertManyAsync(
            [SampleDocument.Create() with { Balance = Money.From(50m, Currency.Pln) }, SampleDocument.Create() with { Id = Guid.CreateVersion7(), Balance = Money.Zero(Currency.Pln) }],
            cancellationToken: Cancellation);
        var stored = await raw.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Cancellation);

        stored.Select(document => document["balance"]["amount"].AsDecimal128.ToString()).Order(StringComparer.Ordinal).ShouldBe(["0.00", "50.00"]);
    }

    // Documents written before amounts were kept at their currency's scale may hold fewer decimals.
    [Fact]
    public async Task Money_StoredWithFewerDecimals_IsReadAtItsCurrencysScale()
    {
        var (typed, raw) = fixture.NewCollection<SampleDocument>();
        var document = SampleDocument.Create().ToBsonDocument();
        document["balance"]["amount"] = new BsonDecimal128(10m);

        await raw.InsertOneAsync(document, cancellationToken: Cancellation);
        var read = await typed.Find(FilterDefinition<SampleDocument>.Empty).SingleAsync(Cancellation);

        read.Balance.Amount.ToString(CultureInfo.InvariantCulture).ShouldBe("10.00");
    }

    [Fact]
    public void InstantWithOffset_CannotBeWritten()
    {
        var document = SampleDocument.Create() with
        {
            CreatedAt = new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.FromHours(1)),
        };

        Should.Throw<BsonSerializationException>(() => document.ToBsonDocument());
    }

    [Fact]
    public void InstantFinerThanAMillisecond_CannotBeWritten()
    {
        var document = SampleDocument.Create() with { UpdatedAt = SampleDocument.Create().UpdatedAt.AddTicks(1) };

        Should.Throw<BsonSerializationException>(() => document.ToBsonDocument());
    }

    [Fact]
    public void UndefinedEnumValue_CannotBeWritten()
    {
        var document = SampleDocument.Create() with { Kind = (SampleKind)7 };

        Should.Throw<BsonSerializationException>(() => document.ToBsonDocument());
    }

    private static void ShouldBeStandardUuid(BsonValue value, Guid expected)
    {
        value.BsonType.ShouldBe(BsonType.Binary);
        value.AsBsonBinaryData.SubType.ShouldBe(BsonBinarySubType.UuidStandard);
        value.AsBsonBinaryData.ToGuid(GuidRepresentation.Standard).ShouldBe(expected);
    }
}
