using FinTrack.Shared.Kernel.Monetary;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace FinTrack.Shared.Infrastructure.Mongo.Serialization;

/// <summary>
/// Stores <see cref="Money"/> as <c>{ amount: Decimal128, currency: "PLN" }</c>.
/// </summary>
/// <remarks>
/// Reading is strict: both elements are required, nothing else is allowed, the currency must be
/// registered and the amount must fit its precision (<see cref="Money.From"/>). Error messages
/// never contain the amount, because exceptions are logged.
/// It also tells LINQ where <c>Amount</c> and <c>Currency</c> are stored, so filters such as
/// <c>x =&gt; x.Balance.Amount &gt; 0</c> work.
/// </remarks>
internal sealed class MoneySerializer : SealedClassSerializerBase<Money>, IBsonDocumentSerializer
{
    private const string AmountElement = "amount";
    private const string CurrencyElement = "currency";

    private static readonly StrictDecimalSerializer AmountSerializer = new();
    private static readonly CurrencySerializer CurrencyValueSerializer = new();

    public bool TryGetMemberSerializationInfo(string memberName, out BsonSerializationInfo serializationInfo)
    {
        serializationInfo = memberName switch
        {
            nameof(Money.Amount) => new BsonSerializationInfo(AmountElement, AmountSerializer, typeof(decimal)),
            nameof(Money.Currency) => new BsonSerializationInfo(CurrencyElement, CurrencyValueSerializer, typeof(Currency)),
            _ => null!,
        };

        return serializationInfo is not null;
    }

    protected override Money DeserializeValue(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        decimal? amount = null;
        Currency? currency = null;

        reader.ReadStartDocument();
        while (reader.ReadBsonType() != BsonType.EndOfDocument)
        {
            var name = reader.ReadName();
            switch (name)
            {
                case AmountElement when amount is null:
                    amount = AmountSerializer.Deserialize(context);
                    break;
                case CurrencyElement when currency is null:
                    currency = CurrencyValueSerializer.Deserialize(context);
                    break;
                default:
                    throw new FormatException($"A stored money value has an unexpected or repeated element '{name}'.");
            }
        }

        reader.ReadEndDocument();

        if (amount is null || currency is null)
        {
            throw new FormatException($"A stored money value needs both '{AmountElement}' and '{CurrencyElement}'.");
        }

        try
        {
            return Money.From(amount.Value, currency);
        }
        catch (ArgumentException exception)
        {
            throw new FormatException($"A stored money value has more decimal places than {currency.Code} allows.", exception);
        }
    }

    protected override void SerializeValue(BsonSerializationContext context, BsonSerializationArgs args, Money value)
    {
        var writer = context.Writer;
        writer.WriteStartDocument();
        writer.WriteName(AmountElement);
        AmountSerializer.Serialize(context, value.Amount);
        writer.WriteName(CurrencyElement);
        CurrencyValueSerializer.Serialize(context, value.Currency);
        writer.WriteEndDocument();
    }
}
