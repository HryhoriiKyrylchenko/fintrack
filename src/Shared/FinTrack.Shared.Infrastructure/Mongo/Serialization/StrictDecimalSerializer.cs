using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace FinTrack.Shared.Infrastructure.Mongo.Serialization;

/// <summary>
/// Stores a <see cref="decimal"/> as <c>Decimal128</c>, which holds every decimal exactly,
/// including its scale (1.10 stays 1.10).
/// </summary>
/// <remarks>
/// Reading accepts <c>Decimal128</c> only. The driver's own serializer also accepts a stored
/// double or integer and converts it, which could turn a binary floating-point value into money
/// without anyone noticing (CLAUDE.md rule 10).
/// </remarks>
internal sealed class StrictDecimalSerializer : StructSerializerBase<decimal>
{
    public override decimal Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        if (reader.CurrentBsonType != BsonType.Decimal128)
        {
            throw new FormatException($"A decimal must be stored as Decimal128, not as {reader.CurrentBsonType}.");
        }

        var stored = reader.ReadDecimal128();
        try
        {
            return Decimal128.ToDecimal(stored);
        }
        catch (OverflowException exception)
        {
            throw new FormatException("A stored Decimal128 is outside the range of a .NET decimal.", exception);
        }
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, decimal value) =>
        context.Writer.WriteDecimal128(new Decimal128(value));
}
