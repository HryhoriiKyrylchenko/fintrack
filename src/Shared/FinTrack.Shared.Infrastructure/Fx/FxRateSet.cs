using FinTrack.Shared.Infrastructure.Mongo;
using FinTrack.Shared.Kernel.Monetary;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace FinTrack.Shared.Infrastructure.Fx;

/// <summary>
/// The rates published for one rate date, against <see cref="Base"/> (always EUR), as stored in
/// <c>ref_fx_rates</c> (plan 1a D8): one document per base and rate date, unique by index.
/// </summary>
/// <remarks>
/// Stored shape, mapped by hand: <c>{ _id, schemaVersion, base: "EUR", date: "yyyy-MM-dd",
/// rates: { PLN: Decimal128, USD: Decimal128 }, fetchedAt }</c>. <c>date</c> sorts as a string in
/// date order. <c>fetchedAt</c> is when the source last returned this date, which decides whether
/// the rates count as fetched today. Rates are kept as published (units per EUR), not rounded.
/// </remarks>
/// <param name="Id">Server-generated version 7 UUID.</param>
/// <param name="SchemaVersion">The stored shape's version.</param>
/// <param name="Base">The base currency; EUR.</param>
/// <param name="Date">The rate date.</param>
/// <param name="Rates">Units of each currency (by code) per unit of <paramref name="Base"/>; every rate above zero.</param>
/// <param name="FetchedAt">When the source last returned this rate date.</param>
public sealed record FxRateSet(
    Guid Id,
    int SchemaVersion,
    Currency Base,
    DateOnly Date,
    IReadOnlyDictionary<string, decimal> Rates,
    DateTimeOffset FetchedAt) : IReferenceDocument
{
    /// <summary>The collection of stored rate sets.</summary>
    public const string CollectionName = "ref_fx_rates";

    /// <summary>The stored shape's version.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly Lock Gate = new();
    private static bool registered;

    /// <inheritdoc />
    public static void RegisterMapping()
    {
        lock (Gate)
        {
            if (registered)
            {
                return;
            }

            BsonClassMap.RegisterClassMap<FxRateSet>(map =>
            {
                map.MapIdMember(set => set.Id);
                map.MapMember(set => set.SchemaVersion).SetElementName("schemaVersion").SetIsRequired(true);
                map.MapMember(set => set.Base).SetElementName("base").SetIsRequired(true);
                map.MapMember(set => set.Date).SetElementName("date").SetIsRequired(true);
                map.MapMember(set => set.Rates).SetElementName("rates").SetIsRequired(true).SetSerializer(new RatesSerializer());
                map.MapMember(set => set.FetchedAt).SetElementName("fetchedAt").SetIsRequired(true);
                map.MapCreator(set => new FxRateSet(set.Id, set.SchemaVersion, set.Base, set.Date, set.Rates, set.FetchedAt));
            });

            registered = true;
        }
    }

    /// <summary>The rates as a document of currency code to <c>Decimal128</c>, read strictly by the shared decimal format.</summary>
    private sealed class RatesSerializer : SerializerBase<IReadOnlyDictionary<string, decimal>>
    {
        public override IReadOnlyDictionary<string, decimal> Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
        {
            var decimals = BsonSerializer.LookupSerializer<decimal>();
            var rates = new Dictionary<string, decimal>(StringComparer.Ordinal);

            context.Reader.ReadStartDocument();
            while (context.Reader.ReadBsonType() != BsonType.EndOfDocument)
            {
                var code = context.Reader.ReadName();
                rates.Add(code, decimals.Deserialize(context));
            }

            context.Reader.ReadEndDocument();
            return rates;
        }

        public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, IReadOnlyDictionary<string, decimal> value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var decimals = BsonSerializer.LookupSerializer<decimal>();

            context.Writer.WriteStartDocument();
            foreach (var (code, rate) in value.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                context.Writer.WriteName(code);
                decimals.Serialize(context, rate);
            }

            context.Writer.WriteEndDocument();
        }
    }
}
