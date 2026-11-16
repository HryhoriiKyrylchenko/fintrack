using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.IntegrationTests.Mongo;

/// <summary>
/// A stored document that does not match the format must fail loudly on read, never be
/// half-read or silently corrected: wrong financial data is worse than an error.
/// </summary>
public sealed class StrictReadingTests(MongoDatabaseFixture fixture)
{
    private static readonly Dictionary<string, Action<BsonDocument>> Corruptions = new()
    {
        ["unknown element"] = document => document["note"] = "added by hand",
        ["unknown enum value"] = document => document["kind"] = "Pension",
        ["enum stored as number"] = document => document["kind"] = 1,
        ["enum with other casing"] = document => document["kind"] = "investment",
        ["unknown currency"] = document => document["currency"] = "XYZ",
        ["money with unknown currency"] = document => document["balance"]["currency"] = "XYZ",
        ["money with too many decimals"] = document => document["balance"]["amount"] = new Decimal128(1.234m),
        ["money with an extra element"] = document => document["balance"]["rate"] = new Decimal128(1m),
        ["money without currency"] = document => document["balance"].AsBsonDocument.Remove("currency"),
        ["money amount as double"] = document => document["balance"]["amount"] = 1.5,
        ["decimal as double"] = document => document["rate"] = 4.3215,
        ["decimal as integer"] = document => document["rate"] = 4,
        ["date in another format"] = document => document["occurredOn"] = "15.01.2026",
        ["date as BSON date"] = document => document["occurredOn"] = new BsonDateTime(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)),
        ["instant as string"] = document => document["createdAt"] = "2026-01-15T09:30:00Z",
        ["uuid in legacy format"] = document => document["ownerId"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.CSharpLegacy),
        ["empty owner"] = document => document["ownerId"] = new BsonBinaryData(Guid.Empty, GuidRepresentation.Standard),
    };

    public static TheoryData<string> CorruptionNames => [.. Corruptions.Keys];

    [Theory]
    [MemberData(nameof(CorruptionNames))]
    public void CorruptedDocument_FailsToRead(string corruption)
    {
        var stored = SampleDocument.Create().ToBsonDocument();
        Corruptions[corruption](stored);

        Should.Throw<FormatException>(() => BsonSerializer.Deserialize<SampleDocument>(stored));
    }

    [Fact]
    public async Task UnknownElementOnTheServer_FailsToRead()
    {
        var (typed, raw) = fixture.NewCollection<SampleDocument>();
        var stored = SampleDocument.Create().ToBsonDocument();
        stored["note"] = "added by hand";
        await raw.InsertOneAsync(stored, cancellationToken: TestContext.Current.CancellationToken);

        await Should.ThrowAsync<FormatException>(() =>
            typed.Find(FilterDefinition<SampleDocument>.Empty).SingleAsync(TestContext.Current.CancellationToken));
    }
}
