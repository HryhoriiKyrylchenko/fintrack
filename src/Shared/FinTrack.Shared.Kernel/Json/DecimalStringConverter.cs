using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FinTrack.Shared.Kernel.Json;

/// <summary>
/// Writes a <see cref="decimal"/> as a JSON string in invariant culture, keeping its scale
/// (<c>1.10m</c> becomes <c>"1.10"</c>).
/// </summary>
/// <remarks>
/// Reading accepts a string only, and only when it is exactly how the value it parses to is
/// written: an optional minus, digits without leading zeros, and an optional fraction. This
/// rejects numbers, exponents, <c>+</c>, whitespace, separators and anything
/// <see cref="decimal.Parse(string)"/> would round silently (more than 28 decimal places).
/// </remarks>
internal sealed class DecimalStringConverter : JsonConverter<decimal>
{
    private const NumberStyles Style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"A decimal must be a JSON string, not {reader.TokenType}.");
        }

        var text = reader.GetString()!;
        return decimal.TryParse(text, Style, CultureInfo.InvariantCulture, out var value)
            && string.Equals(value.ToString(CultureInfo.InvariantCulture), text, StringComparison.Ordinal)
                ? value
                : throw new JsonException("A decimal must be written as digits with an optional minus and fraction, without rounding.");
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}
