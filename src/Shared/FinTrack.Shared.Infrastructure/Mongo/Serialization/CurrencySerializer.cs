using FinTrack.Shared.Kernel.Monetary;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace FinTrack.Shared.Infrastructure.Mongo.Serialization;

/// <summary>
/// Stores a <see cref="Currency"/> as its code, for example <c>"PLN"</c>. The precision is not
/// stored: it belongs to the registered currency, so one code can never have two precisions.
/// </summary>
internal sealed class CurrencySerializer : SealedClassSerializerBase<Currency>
{
    protected override Currency DeserializeValue(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        if (reader.CurrentBsonType != BsonType.String)
        {
            throw new FormatException($"A currency must be stored as its code, not as {reader.CurrentBsonType}.");
        }

        var code = reader.ReadString();
        var currency = Currency.FromCode(code);
        return currency.IsSuccess
            ? currency.Value
            : throw new FormatException($"The stored currency code '{code}' is not registered.");
    }

    protected override void SerializeValue(BsonSerializationContext context, BsonSerializationArgs args, Currency value) =>
        context.Writer.WriteString(value.Code);
}
