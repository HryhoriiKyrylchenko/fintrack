using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FinTrack.Shared.Kernel.Json;

/// <summary>
/// Writes a business date (<see cref="DateOnly"/>) as <c>yyyy-MM-dd</c>. Reading accepts exactly
/// that format; a date has no time zone, so it cannot shift.
/// </summary>
internal sealed class IsoDateOnlyJsonConverter : JsonConverter<DateOnly>
{
    private const string Format = "yyyy-MM-dd";

    public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"A date must be a JSON string, not {reader.TokenType}.");
        }

        return DateOnly.TryParseExact(reader.GetString(), Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new JsonException($"A date must have the format {Format}.");
    }

    public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString(Format, CultureInfo.InvariantCulture));
    }
}
