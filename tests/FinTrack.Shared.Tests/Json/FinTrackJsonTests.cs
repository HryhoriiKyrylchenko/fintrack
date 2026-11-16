using System.Text.Json;
using FinTrack.Shared.Kernel.Json;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Tests.Ids;

namespace FinTrack.Shared.Tests.Json;

/// <summary>
/// The JSON contract of the Api (ARCHITECTURE 2.7): the exact shape of every kernel type, and
/// strict reading, mirroring the storage format (a value that does not fit is rejected, never
/// converted).
/// </summary>
public class FinTrackJsonTests
{
    private const string IdText = "0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b";

    private static readonly JsonContractSample Sample = new(
        TestId.From(Guid.Parse(IdText)),
        Money.From(1234.50m, Currency.Pln),
        Currency.Eur,
        4.2512m,
        new DateOnly(2026, 1, 15),
        new DateTimeOffset(2026, 1, 15, 9, 30, 0, 123, TimeSpan.Zero),
        JsonContractKind.Deposit,
        null);

    private const string SampleJson =
        "{\"id\":\"" + IdText + "\",\"price\":{\"amount\":\"1234.50\",\"currency\":\"PLN\"},\"currency\":\"EUR\",\"rate\":\"4.2512\","
        + "\"occurredOn\":\"2026-01-15\",\"recordedAt\":\"2026-01-15T09:30:00.123Z\",\"kind\":\"Deposit\",\"note\":null}";

    [Fact]
    public void Write_HasTheContractShape() =>
        Serialize(Sample).ShouldBe(SampleJson);

    [Fact]
    public void Read_ReturnsTheSameValue() =>
        Deserialize<JsonContractSample>(SampleJson).ShouldBe(Sample);

    [Fact]
    public void Options_AreReadOnly() =>
        FinTrackJson.Options.IsReadOnly.ShouldBeTrue();

    [Fact]
    public void Configure_TurnsOtherOptionsIntoTheContract()
    {
        // Web defaults (what ASP.NET Core starts from) read numbers from strings and ignore case.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        FinTrackJson.Configure(options);

        JsonSerializer.Serialize(Sample, options).ShouldBe(SampleJson);
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<JsonContractSample>(SampleJson.Replace("\"note\"", "\"Note\"", StringComparison.Ordinal), options));
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<CountSample>("""{"count":"5"}""", options));
    }

    // --- Decimals: strings with their exact scale ---

    [Theory]
    [InlineData("1.10")]
    [InlineData("0")]
    [InlineData("0.00")]
    [InlineData("-12.5")]
    [InlineData("0.000000000000000001")]
    [InlineData("123456789.123456789012345678")]
    [InlineData("79228162514264337593543950335")]
    [InlineData("-79228162514264337593543950335")]
    public void Decimal_RoundTripsWithItsScale(string text)
    {
        var json = $"\"{text}\"";

        var value = Deserialize<decimal>(json);

        Serialize(value).ShouldBe(json);
    }

    [Theory]
    [InlineData("1.10")]
    [InlineData("-3")]
    [InlineData("\"1e2\"")]
    [InlineData("\"1E2\"")]
    [InlineData("\"+1\"")]
    [InlineData("\" 1\"")]
    [InlineData("\"1 \"")]
    [InlineData("\"01\"")]
    [InlineData("\".5\"")]
    [InlineData("\"1.\"")]
    [InlineData("\"1,5\"")]
    [InlineData("\"1_000\"")]
    [InlineData("\"\"")]
    [InlineData("\"NaN\"")]
    [InlineData("\"0x10\"")]
    [InlineData("null")]
    [InlineData("true")]
    // 29 decimal places: decimal.Parse would round it silently.
    [InlineData("\"0.12345678901234567890123456789\"")]
    // One more than decimal.MaxValue.
    [InlineData("\"79228162514264337593543950336\"")]
    public void Decimal_ThatIsNotAnExactDecimalString_IsRejected(string json) =>
        Should.Throw<JsonException>(() => Deserialize<decimal>(json));

    [Fact]
    public void NullableDecimal_ReadsNullAndAString()
    {
        Deserialize<decimal?>("null").ShouldBeNull();
        Deserialize<decimal?>("\"2.50\"").ShouldBe(2.50m);
        Should.Throw<JsonException>(() => Deserialize<decimal?>("2.5"));
    }

    // --- Currency and Money ---

    [Fact]
    public void Currency_IsItsCode()
    {
        Serialize(Currency.Usd).ShouldBe("\"USD\"");
        Deserialize<Currency>("\"USD\"").ShouldBe(Currency.Usd);
    }

    [Theory]
    [InlineData("\"usd\"")]
    [InlineData("\"XYZ\"")]
    [InlineData("\"\"")]
    [InlineData("840")]
    [InlineData("{\"code\":\"USD\"}")]
    public void Currency_ThatIsNotARegisteredCode_IsRejected(string json) =>
        Should.Throw<JsonException>(() => Deserialize<Currency>(json));

    [Fact]
    public void Money_IsAmountAndCurrency()
    {
        Serialize(Money.From(-0.10m, Currency.Eur)).ShouldBe("""{"amount":"-0.10","currency":"EUR"}""");
        Deserialize<Money>("""{"currency":"EUR","amount":"-0.10"}""").ShouldBe(Money.From(-0.10m, Currency.Eur));
    }

    [Theory]
    [InlineData("""{"amount":1.10,"currency":"PLN"}""")]
    [InlineData("""{"amount":"1.10"}""")]
    [InlineData("""{"currency":"PLN"}""")]
    [InlineData("""{"amount":"1.10","currency":"PLN","note":"x"}""")]
    [InlineData("""{"amount":"1.10","amount":"2.10","currency":"PLN"}""")]
    [InlineData("""{"Amount":"1.10","currency":"PLN"}""")]
    [InlineData("""{"amount":"1.10","currency":"XYZ"}""")]
    [InlineData("""{"amount":null,"currency":"PLN"}""")]
    [InlineData("""{"amount":"1.10","currency":null}""")]
    [InlineData("""{"amount":{"value":"1.10"},"currency":"PLN"}""")]
    [InlineData("\"1.10 PLN\"")]
    [InlineData("[\"1.10\",\"PLN\"]")]
    public void Money_ThatDoesNotFitTheContract_IsRejected(string json) =>
        Should.Throw<JsonException>(() => Deserialize<Money>(json));

    [Fact]
    public void Money_WithMoreDecimalsThanTheCurrency_IsRejected_WithoutTheAmountInTheMessage()
    {
        var exception = Should.Throw<JsonException>(() => Deserialize<Money>("""{"amount":"98765.432","currency":"PLN"}"""));

        exception.Message.ShouldContain("PLN");
        exception.Message.ShouldNotContain("98765");
    }

    // --- Identifiers ---

    [Fact]
    public void StronglyTypedId_IsTheGuidString()
    {
        var id = TestId.From(Guid.Parse(IdText));

        Serialize(id).ShouldBe($"\"{IdText}\"");
        Deserialize<TestId>($"\"{IdText}\"").ShouldBe(id);
        Deserialize<TestId?>("null").ShouldBeNull();
    }

    [Theory]
    [InlineData("\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("\"0190a1b2c3d47e5f8a6b7c8d9e0f1a2b\"")]
    [InlineData("\"{0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b}\"")]
    [InlineData("\"not-a-guid\"")]
    [InlineData("\"\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void StronglyTypedId_ThatIsNotANonEmptyGuid_IsRejected(string json) =>
        Should.Throw<JsonException>(() => Deserialize<TestId>(json));

    // --- Business dates and instants ---

    [Fact]
    public void DateOnly_IsIsoDate()
    {
        Serialize(new DateOnly(2026, 3, 5)).ShouldBe("\"2026-03-05\"");
        Deserialize<DateOnly>("\"2026-03-05\"").ShouldBe(new DateOnly(2026, 3, 5));
    }

    [Theory]
    [InlineData("\"2026-3-5\"")]
    [InlineData("\"2026-03-05T00:00:00\"")]
    [InlineData("\"05.03.2026\"")]
    [InlineData("\"2026-02-30\"")]
    [InlineData("20260305")]
    public void DateOnly_InAnyOtherFormat_IsRejected(string json) =>
        Should.Throw<JsonException>(() => Deserialize<DateOnly>(json));

    [Theory]
    [InlineData(0, "\"2026-01-15T09:30:00Z\"")]
    [InlineData(123, "\"2026-01-15T09:30:00.123Z\"")]
    [InlineData(100, "\"2026-01-15T09:30:00.1Z\"")]
    public void Instant_IsUtcIso8601(int milliseconds, string json)
    {
        var instant = new DateTimeOffset(2026, 1, 15, 9, 30, 0, milliseconds, TimeSpan.Zero);

        Serialize(instant).ShouldBe(json);
        Deserialize<DateTimeOffset>(json).ShouldBe(instant);
        Deserialize<DateTimeOffset>(json).Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Instant_KeepsTicksBelowAMillisecond()
    {
        var instant = new DateTimeOffset(2026, 1, 15, 9, 30, 0, TimeSpan.Zero).AddTicks(1_234_567);

        Serialize(instant).ShouldBe("\"2026-01-15T09:30:00.1234567Z\"");
        Deserialize<DateTimeOffset>(Serialize(instant)).ShouldBe(instant);
    }

    [Fact]
    public void Instant_WithAnOffset_CannotBeWritten() =>
        Should.Throw<InvalidOperationException>(() => Serialize(new DateTimeOffset(2026, 1, 15, 9, 30, 0, TimeSpan.FromHours(1))));

    [Theory]
    [InlineData("\"2026-01-15T09:30:00+00:00\"")]
    [InlineData("\"2026-01-15T10:30:00+01:00\"")]
    [InlineData("\"2026-01-15T09:30:00\"")]
    [InlineData("\"2026-01-15T09:30:00z\"")]
    [InlineData("\"2026-01-15\"")]
    [InlineData("\"15.01.2026 09:30Z\"")]
    [InlineData("1768469400000")]
    public void Instant_ThatIsNotUtcIso8601_IsRejected(string json) =>
        Should.Throw<JsonException>(() => Deserialize<DateTimeOffset>(json));

    // --- Enums ---

    [Fact]
    public void Enum_IsItsName()
    {
        Serialize(JsonContractKind.Spend).ShouldBe("\"Spend\"");
        Deserialize<JsonContractKind>("\"Spend\"").ShouldBe(JsonContractKind.Spend);
        Deserialize<JsonContractKind?>("null").ShouldBeNull();
    }

    [Theory]
    [InlineData("\"spend\"")]
    [InlineData("\"SPEND\"")]
    [InlineData("\"1\"")]
    [InlineData("1")]
    [InlineData("\"Deposit, Spend\"")]
    [InlineData("\"Transfer\"")]
    [InlineData("\"\"")]
    public void Enum_ThatIsNotADefinedName_IsRejected(string json) =>
        Should.Throw<JsonException>(() => Deserialize<JsonContractKind>(json));

    [Fact]
    public void Enum_UndefinedValue_CannotBeWritten() =>
        Should.Throw<InvalidOperationException>(() => Serialize((JsonContractKind)42));

    // --- Objects ---

    [Theory]
    [InlineData("""{"name":"x","note":null,"extra":1}""")]
    [InlineData("""{"Name":"x","note":null}""")]
    [InlineData("""{"note":null}""")]
    [InlineData("""{"name":"x"}""")]
    [InlineData("""{"name":"x","name":"y","note":null}""")]
    [InlineData("""{"name":"x","note":null,}""")]
    [InlineData("""{"name":"x","note":null/* x */}""")]
    [InlineData("""{"name":null,"note":null}""")]
    [InlineData("""["x",null]""")]
    public void Object_ThatDoesNotFitTheContract_IsRejected(string json) =>
        Should.Throw<JsonException>(() => Deserialize<NameSample>(json));

    [Fact]
    public void Object_WithAnOptionalPropertyMissing_ReadsTheDefault() =>
        Deserialize<OptionalSample>("""{"name":"x"}""").ShouldBe(new OptionalSample("x"));

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, FinTrackJson.Options);

    private static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, FinTrackJson.Options);
}

/// <summary>Every kernel type the contract defines, in one object.</summary>
internal sealed record JsonContractSample(
    TestId Id,
    Money Price,
    Currency Currency,
    decimal Rate,
    DateOnly OccurredOn,
    DateTimeOffset RecordedAt,
    JsonContractKind Kind,
    string? Note);

internal enum JsonContractKind
{
    Deposit,
    Spend,
}

internal sealed record CountSample(int Count);

internal sealed record NameSample(string Name, string? Note);

internal sealed record OptionalSample(string Name, string? Note = null);
