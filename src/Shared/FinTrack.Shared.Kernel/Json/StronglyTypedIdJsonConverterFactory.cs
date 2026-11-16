using System.Text.Json;
using System.Text.Json.Serialization;
using FinTrack.Shared.Kernel.Ids;

namespace FinTrack.Shared.Kernel.Json;

/// <summary>
/// Supplies <see cref="StronglyTypedIdJsonConverter{TId}"/> for every type that implements
/// <see cref="IStronglyTypedId{TSelf}"/>, so a new identifier type needs no registration.
/// </summary>
internal sealed class StronglyTypedIdJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);

        return typeToConvert.IsValueType && typeToConvert.GetInterfaces().Any(implemented =>
            implemented.IsGenericType
            && implemented.GetGenericTypeDefinition() == typeof(IStronglyTypedId<>)
            && implemented.GenericTypeArguments[0] == typeToConvert);
    }

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(StronglyTypedIdJsonConverter<>).MakeGenericType(typeToConvert))!;
}

/// <summary>
/// Writes a strongly typed ID as the GUID it wraps (<c>D</c> format, lower case). The wrapper type
/// is not written. Reading accepts the <c>D</c> format only and never an empty GUID. Whether a
/// client-sent ID is a version 7 UUID is checked by the use case (<see cref="StronglyTypedId.FromClient{TId}"/>).
/// </summary>
internal sealed class StronglyTypedIdJsonConverter<TId> : JsonConverter<TId>
    where TId : struct, IStronglyTypedId<TId>
{
    private const string Format = "D";

    public override TId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"A {typeof(TId).Name} must be a GUID string, not {reader.TokenType}.");
        }

        if (!Guid.TryParseExact(reader.GetString(), Format, out var value))
        {
            throw new JsonException($"A {typeof(TId).Name} must be a GUID in the format xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx.");
        }

        return value == Guid.Empty
            ? throw new JsonException($"A {typeof(TId).Name} cannot be empty.")
            : TId.From(value);
    }

    public override void Write(Utf8JsonWriter writer, TId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Value.ToString(Format));
    }
}
