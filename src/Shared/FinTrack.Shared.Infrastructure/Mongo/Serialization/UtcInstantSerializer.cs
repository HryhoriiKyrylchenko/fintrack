using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace FinTrack.Shared.Infrastructure.Mongo.Serialization;

/// <summary>
/// Stores an instant (<see cref="DateTimeOffset"/>) as a BSON date: UTC, whole milliseconds.
/// </summary>
/// <remarks>
/// A BSON date cannot hold an offset or anything finer than a millisecond. Instead of dropping
/// either silently, writing such a value throws: every instant comes from <c>IClock</c>, which
/// returns UTC in whole milliseconds, so a value read back always equals the value written.
/// </remarks>
internal sealed class UtcInstantSerializer : StructSerializerBase<DateTimeOffset>
{
    public override DateTimeOffset Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        if (reader.CurrentBsonType != BsonType.DateTime)
        {
            throw new FormatException($"An instant must be stored as a BSON date, not as {reader.CurrentBsonType}.");
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(reader.ReadDateTime());
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new BsonSerializationException("Only UTC instants can be stored; take the time from IClock.");
        }

        if (value.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new BsonSerializationException("Only whole milliseconds can be stored; take the time from IClock.");
        }

        context.Writer.WriteDateTime(value.ToUnixTimeMilliseconds());
    }
}
