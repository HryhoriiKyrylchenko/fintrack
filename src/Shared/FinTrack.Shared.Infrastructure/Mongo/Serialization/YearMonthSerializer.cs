using FinTrack.Shared.Kernel.Time;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace FinTrack.Shared.Infrastructure.Mongo.Serialization;

/// <summary>
/// Stores a business month (<see cref="YearMonth"/>) as the string <c>yyyy-MM</c>. Like a date, it
/// has no time zone and sorts correctly as text. Reading accepts exactly that spelling
/// (<see cref="YearMonth.TryParse"/>); any other string or BSON type fails.
/// </summary>
internal sealed class YearMonthSerializer : StructSerializerBase<YearMonth>
{
    public override YearMonth Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        if (reader.CurrentBsonType != BsonType.String)
        {
            throw new FormatException($"A month must be stored as a string, not as {reader.CurrentBsonType}.");
        }

        return YearMonth.TryParse(reader.ReadString(), out var month)
            ? month
            : throw new FormatException("A stored month must have the format yyyy-MM.");
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, YearMonth value) =>
        context.Writer.WriteString(value.ToString());
}
