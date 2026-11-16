using System.Text.Json;
using System.Text.Json.Serialization;

namespace FinTrack.Shared.Kernel.Json;

/// <summary>Supplies <see cref="EnumNameJsonConverter{TEnum}"/> for every enum, so a new enum needs no registration.</summary>
internal sealed class EnumNameJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);
        return typeToConvert.IsEnum;
    }

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(EnumNameJsonConverter<>).MakeGenericType(typeToConvert))!;
}

/// <summary>
/// Writes an enum value as its name (<c>"Deposit"</c>); names survive reordering of the members,
/// numbers would not.
/// </summary>
/// <remarks>
/// Reading accepts a defined name with exact case only. The framework's own converter also
/// accepts other casing and comma-separated combinations, which would silently map to a value.
/// </remarks>
internal sealed class EnumNameJsonConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"A {typeof(TEnum).Name} must be its name as a JSON string, not {reader.TokenType}.");
        }

        var name = reader.GetString();
        return Enum.GetNames<TEnum>().Contains(name, StringComparer.Ordinal)
            ? Enum.Parse<TEnum>(name!)
            : throw new JsonException($"The value is not a {typeof(TEnum).Name}.");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var name = Enum.GetName(value)
            ?? throw new InvalidOperationException($"Only defined {typeof(TEnum).Name} values can be written.");

        writer.WriteStringValue(name);
    }
}
