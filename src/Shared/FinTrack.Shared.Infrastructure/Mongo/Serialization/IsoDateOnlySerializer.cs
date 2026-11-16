using System.Globalization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace FinTrack.Shared.Infrastructure.Mongo.Serialization;

/// <summary>
/// Stores a business date (<see cref="DateOnly"/>) as an ISO 8601 string, <c>yyyy-MM-dd</c>.
/// A string has no time zone, so the date cannot shift; it also sorts correctly as text.
/// </summary>
internal sealed class IsoDateOnlySerializer : StructSerializerBase<DateOnly>
{
    private const string Format = "yyyy-MM-dd";

    public override DateOnly Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        if (reader.CurrentBsonType != BsonType.String)
        {
            throw new FormatException($"A date must be stored as a string, not as {reader.CurrentBsonType}.");
        }

        var text = reader.ReadString();
        return DateOnly.TryParseExact(text, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new FormatException($"A stored date must have the format {Format}.");
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, DateOnly value) =>
        context.Writer.WriteString(value.ToString(Format, CultureInfo.InvariantCulture));
}
