using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FinTrack.Shared.Kernel.Json;

/// <summary>
/// Writes an instant (<see cref="DateTimeOffset"/>) as ISO 8601 in UTC ending in <c>Z</c>, with
/// the fraction of a second only as long as needed (<c>2026-01-15T09:30:00.123Z</c>).
/// </summary>
/// <remarks>
/// Reading accepts ISO 8601 ending in <c>Z</c> only: an instant without an offset is ambiguous, and
/// the contract has one spelling of UTC. Writing a value with an offset throws: every instant
/// comes from <c>IClock</c> or the database, both UTC, so an offset means a bug.
/// </remarks>
internal sealed class UtcInstantJsonConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"An instant must be a JSON string, not {reader.TokenType}.");
        }

        var endsInZ = reader.ValueSpan.Length > 0 && reader.ValueSpan[^1] == (byte)'Z' && !reader.ValueIsEscaped;
        return endsInZ && reader.TryGetDateTimeOffset(out var instant)
            ? instant.ToUniversalTime()
            : throw new JsonException("An instant must be ISO 8601 in UTC, ending in 'Z'.");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Only UTC instants can be written; take the time from IClock.");
        }

        writer.WriteStringValue(value.ToString(Format, CultureInfo.InvariantCulture));
    }
}
