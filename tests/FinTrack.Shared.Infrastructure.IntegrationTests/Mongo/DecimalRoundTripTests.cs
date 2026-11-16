using System.Globalization;
using CsCheck;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.IntegrationTests.Mongo;

/// <summary>
/// Every <see cref="decimal"/> must come back exactly, including its scale (1.10 stays 1.10),
/// because amounts are money and crypto quantities have up to 18 decimal places.
/// </summary>
public sealed class DecimalRoundTripTests(MongoDatabaseFixture fixture)
{
    public static TheoryData<string> EdgeValues =>
    [
        "0.000000000000000001",
        "-0.000000000000000001",
        "123456789.123456789012345678",
        "1.100000000000000000",
        "0",
        "0.00",
        "79228162514264337593543950335",
        "-79228162514264337593543950335",
        "0.0000000000000000000000000001",
    ];

    [Theory]
    [MemberData(nameof(EdgeValues))]
    public async Task EdgeValue_RoundTripsThroughTheServerExactly(string text)
    {
        var value = decimal.Parse(text, CultureInfo.InvariantCulture);
        var (typed, raw) = fixture.NewCollection<DecimalDocument>();

        await typed.InsertOneAsync(new DecimalDocument(Guid.CreateVersion7(), 0, value), cancellationToken: TestContext.Current.CancellationToken);
        var read = await typed.Find(FilterDefinition<DecimalDocument>.Empty).SingleAsync(TestContext.Current.CancellationToken);
        var stored = await raw.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(TestContext.Current.CancellationToken);

        stored["value"].BsonType.ShouldBe(BsonType.Decimal128);
        ShouldBeExactly(read.Value, value);
    }

    [Fact]
    public async Task GeneratedValues_RoundTripThroughTheServerExactly()
    {
        var values = Gen.Decimal.Array[500].Single();
        var (typed, _) = fixture.NewCollection<DecimalDocument>();

        await typed.InsertManyAsync(
            values.Select((value, index) => new DecimalDocument(Guid.CreateVersion7(), index, value)),
            cancellationToken: TestContext.Current.CancellationToken);
        var read = await typed.Find(FilterDefinition<DecimalDocument>.Empty)
            .SortBy(document => document.Index)
            .ToListAsync(TestContext.Current.CancellationToken);

        read.Count.ShouldBe(values.Length);
        for (var index = 0; index < values.Length; index++)
        {
            ShouldBeExactly(read[index].Value, values[index]);
        }
    }

    [Fact]
    public void AnyDecimal_RoundTripsThroughBsonExactly() =>
        Gen.Decimal.Sample(value =>
        {
            var read = BsonSerializer.Deserialize<DecimalDocument>(new DecimalDocument(Guid.Empty, 0, value).ToBsonDocument());
            return read.Value == value && SameText(read.Value, value);
        });

    private static void ShouldBeExactly(decimal actual, decimal expected)
    {
        actual.ShouldBe(expected);
        actual.ToString(CultureInfo.InvariantCulture).ShouldBe(expected.ToString(CultureInfo.InvariantCulture));
    }

    private static bool SameText(decimal left, decimal right) =>
        left.ToString(CultureInfo.InvariantCulture) == right.ToString(CultureInfo.InvariantCulture);
}
