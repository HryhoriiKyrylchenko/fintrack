using System.Text.Json;
using System.Text.Json.Serialization;
using FinTrack.Shared.Kernel.Time;

namespace FinTrack.Shared.Kernel.Json;

/// <summary>
/// Writes a business month (<see cref="YearMonth"/>) as <c>yyyy-MM</c> (<c>"2026-11"</c>). Reading
/// accepts exactly that spelling (<see cref="YearMonth.TryParse"/>); a number, a day, another
/// separator, a sign, spaces or a value out of range are rejected, never converted.
/// </summary>
internal sealed class YearMonthJsonConverter : JsonConverter<YearMonth>
{
    public override YearMonth Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"A month must be a JSON string, not {reader.TokenType}.");
        }

        return YearMonth.TryParse(reader.GetString(), out var month)
            ? month
            : throw new JsonException("A month must have the format yyyy-MM.");
    }

    public override void Write(Utf8JsonWriter writer, YearMonth value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}
