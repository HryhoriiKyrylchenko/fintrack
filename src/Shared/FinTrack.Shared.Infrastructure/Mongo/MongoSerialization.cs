using FinTrack.Shared.Infrastructure.Mongo.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;

namespace FinTrack.Shared.Infrastructure.Mongo;

/// <summary>
/// The storage format of every document (ARCHITECTURE 2.2). It is effectively permanent: the
/// journal lives forever, so a later change needs an upcaster, not a migration.
/// </summary>
/// <remarks>
/// <para>The format:</para>
/// <list type="bullet">
/// <item><description>element names in camelCase; <c>Id</c> is stored as <c>_id</c>;</description></item>
/// <item><description><see cref="Guid"/> and every strongly typed ID as a standard UUID (binary subtype 4);</description></item>
/// <item><description><see cref="decimal"/> as <c>Decimal128</c>, exact; a stored double or integer is rejected;</description></item>
/// <item><description>enums as their names; a stored number or unknown name is rejected;</description></item>
/// <item><description><see cref="DateOnly"/> as an ISO string <c>yyyy-MM-dd</c>; <c>YearMonth</c> as the string <c>yyyy-MM</c>,
/// read in that spelling only;</description></item>
/// <item><description><see cref="DateTimeOffset"/> as a BSON date: UTC, whole milliseconds (as <c>IClock</c> returns);</description></item>
/// <item><description><c>Currency</c> as its code; <c>Money</c> as <c>{ amount, currency }</c>;</description></item>
/// <item><description>an element the type does not know makes reading fail.</description></item>
/// </list>
/// <para>
/// Unlike EF Core's model, these settings are global to the process and are fixed for a type the
/// first time it is serialized, so they must be registered before any MongoDB use.
/// <see cref="MongoServiceCollectionExtensions.AddFinTrackMongo"/> does that.
/// </para>
/// </remarks>
public static class MongoSerialization
{
    private const string ConventionPackName = "FinTrack";
    private const string OwnNamespacePrefix = "FinTrack.";

    private static readonly Lock Gate = new();
    private static bool registered;

    /// <summary>Registers the format. Safe to call more than once and from several threads.</summary>
    public static void Register()
    {
        lock (Gate)
        {
            if (registered)
            {
                return;
            }

            BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
            BsonSerializer.RegisterSerializer(new StrictDecimalSerializer());
            BsonSerializer.RegisterSerializer(new IsoDateOnlySerializer());
            BsonSerializer.RegisterSerializer(new YearMonthSerializer());
            BsonSerializer.RegisterSerializer(new UtcInstantSerializer());
            BsonSerializer.RegisterSerializer(new CurrencySerializer());
            BsonSerializer.RegisterSerializer(new MoneySerializer());
            BsonSerializer.RegisterSerializationProvider(new StronglyTypedIdSerializationProvider());
            BsonSerializer.RegisterSerializationProvider(new EnumNameSerializationProvider());

            var conventions = new ConventionPack
            {
                new CamelCaseElementNameConvention(),
                new IgnoreExtraElementsConvention(false),
            };

            // Only our own types: the driver's internal types keep their defaults.
            ConventionRegistry.Register(ConventionPackName, conventions, IsOwnType);

            registered = true;
        }
    }

    /// <summary>Whether <paramref name="type"/> is declared in FinTrack code (not the driver or the framework).</summary>
    internal static bool IsOwnType(Type type) =>
        type.FullName?.StartsWith(OwnNamespacePrefix, StringComparison.Ordinal) == true;
}
