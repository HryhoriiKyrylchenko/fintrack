using FinTrack.Budget.Domain.Journal;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace FinTrack.Budget.Infrastructure;

/// <summary>
/// Stores an <see cref="EntrySource"/> as its text (<c>"Manual"</c>, <c>"Import:binance"</c>). Reading is
/// strict: anything that is not exactly one of the forms fails.
/// </summary>
internal sealed class EntrySourceSerializer : StructSerializerBase<EntrySource>
{
    public override EntrySource Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        if (reader.CurrentBsonType != BsonType.String)
        {
            throw new FormatException($"An entry source must be stored as a string, not as {reader.CurrentBsonType}.");
        }

        return EntrySource.TryParse(reader.ReadString(), out var source)
            ? source
            : throw new FormatException("The stored entry source is not 'Manual', 'Import:<name>' or 'Agent:<name>'.");
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, EntrySource value) =>
        context.Writer.WriteString(value.Value ?? throw new BsonSerializationException("An entry source must be set before it is stored."));
}
