using System.Text.Json;
using System.Text.Json.Serialization;
using FinTrack.Shared.Kernel.Monetary;

namespace FinTrack.Shared.Kernel.Json;

/// <summary>
/// Writes a <see cref="Currency"/> as its code (<c>"PLN"</c>). Reading accepts a registered code
/// only, with exact case; the precision comes from the registered currency.
/// </summary>
internal sealed class CurrencyJsonConverter : JsonConverter<Currency>
{
    public override Currency Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"A currency must be its code as a JSON string, not {reader.TokenType}.");
        }

        var currency = Currency.FromCode(reader.GetString());
        return currency.IsSuccess
            ? currency.Value
            : throw new JsonException("The currency code is not registered.");
    }

    public override void Write(Utf8JsonWriter writer, Currency value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStringValue(value.Code);
    }
}
