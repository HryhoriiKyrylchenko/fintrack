using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace FinTrack.Shared.Infrastructure.Mongo.Serialization;

/// <summary>
/// Stores an enum value as its name, for example <c>"Deposit"</c>. Names survive reordering of
/// the enum members; numbers would not.
/// </summary>
/// <remarks>
/// Reading accepts a defined name only, with exact case. The driver's own serializer also accepts
/// numbers, which would silently map to whatever member has that number today.
/// </remarks>
internal sealed class EnumNameSerializer<TEnum> : StructSerializerBase<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        if (reader.CurrentBsonType != BsonType.String)
        {
            throw new FormatException($"A {typeof(TEnum).Name} must be stored as its name, not as {reader.CurrentBsonType}.");
        }

        var name = reader.ReadString();

        // Enum.TryParse would also accept numbers ("1") and other casing; a defined name is required.
        return Enum.GetNames<TEnum>().Contains(name, StringComparer.Ordinal)
            ? Enum.Parse<TEnum>(name)
            : throw new FormatException($"'{name}' is not a {typeof(TEnum).Name}.");
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, TEnum value)
    {
        var name = Enum.GetName(value)
            ?? throw new BsonSerializationException($"Only defined {typeof(TEnum).Name} values can be stored.");

        context.Writer.WriteString(name);
    }
}
