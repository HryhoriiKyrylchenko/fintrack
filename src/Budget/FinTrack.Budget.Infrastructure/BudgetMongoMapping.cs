using System.Linq.Expressions;
using System.Reflection;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.Settings;
using FinTrack.Shared.Infrastructure.Mongo;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;

namespace FinTrack.Budget.Infrastructure;

/// <summary>
/// How Budget's domain types are stored. The stored format is permanent (ARCHITECTURE 2.2), so
/// every type is mapped explicitly here; Domain has no database attributes.
/// </summary>
/// <remarks>
/// <para>The pattern, reused by every Budget document type:</para>
/// <list type="bullet">
/// <item><description>every stored property is mapped by hand with an explicit element name, so renaming a C# property never changes what is stored (no automatic mapping, no naming convention);</description></item>
/// <item><description>every element is required, except a field that is optional by design (<see cref="MapOptional"/>): it is not written when empty and reads as empty when missing, so adding such a field later needs no upcaster (ARCHITECTURE 4.1);</description></item>
/// <item><description>the type's private constructor restores a stored document; its parameters are matched to the mapped properties by name;</description></item>
/// <item><description>unknown elements make reading fail, as for every stored type;</description></item>
/// <item><description>journal entry types share <c>budget_journal</c>: the root <see cref="JournalEntry"/> maps the common
/// fields, each type its own, and the element <c>type</c> holds the stable name from <see cref="JournalEntryTypes"/>,
/// never the CLR name (plan 1a D1; a guard test requires a name for every entry type).</description></item>
/// </list>
/// <para>
/// Value formats (IDs, currencies, instants) come from the shared format in
/// <see cref="MongoSerialization"/>. Like that format, class maps are global to the process and
/// must be registered before the first use; <see cref="BudgetServiceCollectionExtensions.AddBudget"/> does it.
/// </para>
/// </remarks>
public static class BudgetMongoMapping
{
    /// <summary>The element that holds an entry's type name.</summary>
    public const string TypeElement = "type";

    private static readonly Lock Gate = new();
    private static bool registered;

    /// <summary>
    /// The stable stored name of each journal entry type (ARCHITECTURE 4.2). Written out by hand: a
    /// stored name must never follow a renamed class. Every concrete entry type must be listed.
    /// </summary>
    public static IReadOnlyDictionary<Type, string> JournalEntryTypes { get; } = new Dictionary<Type, string>
    {
        [typeof(OpeningBalance)] = "OpeningBalance",
        [typeof(Deposit)] = "Deposit",
        [typeof(Spend)] = "Spend",
        [typeof(Transfer)] = "Transfer",
    };

    /// <summary>Registers the class maps. Safe to call more than once and from several threads.</summary>
    public static void Register()
    {
        lock (Gate)
        {
            if (registered)
            {
                return;
            }

            MongoSerialization.Register();
            BsonClassMap.RegisterClassMap<OwnerSettings>(MapOwnerSettings);
            BsonClassMap.RegisterClassMap<Bucket>(MapBucket);

            BsonSerializer.RegisterSerializer(new EntrySourceSerializer());
            BsonSerializer.RegisterDiscriminatorConvention(typeof(JournalEntry), new ScalarDiscriminatorConvention(TypeElement));
            BsonClassMap.RegisterClassMap<JournalEntry>(MapJournalEntry);
            BsonClassMap.RegisterClassMap<SingleBucketEntry>(MapSingleBucketEntry);
            BsonClassMap.RegisterClassMap<OpeningBalance>(MapEntryType);
            BsonClassMap.RegisterClassMap<Deposit>(MapEntryType);
            BsonClassMap.RegisterClassMap<Spend>(MapEntryType);
            BsonClassMap.RegisterClassMap<Transfer>(MapTransfer);

            registered = true;
        }
    }

    private static void MapOwnerSettings(BsonClassMap<OwnerSettings> map)
    {
        map.MapIdMember(settings => settings.Id);
        Map(map, settings => settings.OwnerId, "ownerId");
        Map(map, settings => settings.SchemaVersion, "schemaVersion");
        Map(map, settings => settings.Version, "version");
        Map(map, settings => settings.CreatedAt, "createdAt");
        Map(map, settings => settings.UpdatedAt, "updatedAt");
        Map(map, settings => settings.ReportingCurrency, "reportingCurrency");
        Map(map, settings => settings.TimeZone, "timeZone");
        Map(map, settings => settings.Locale, "locale");
        map.SetIgnoreExtraElements(false);
        MapPrivateConstructor(map);
    }

    private static void MapBucket(BsonClassMap<Bucket> map)
    {
        map.MapIdMember(bucket => bucket.Id);
        Map(map, bucket => bucket.OwnerId, "ownerId");
        Map(map, bucket => bucket.SchemaVersion, "schemaVersion");
        Map(map, bucket => bucket.Version, "version");
        Map(map, bucket => bucket.CreatedAt, "createdAt");
        Map(map, bucket => bucket.UpdatedAt, "updatedAt");
        MapOptional(map, bucket => bucket.Name, "name");
        Map(map, bucket => bucket.Currency, "currency");
        Map(map, bucket => bucket.Kind, "kind");
        MapOptional(map, bucket => bucket.Profile, "profile");
        Map(map, bucket => bucket.Status, "status");
        Map(map, bucket => bucket.SystemRole, "systemRole");
        map.SetIgnoreExtraElements(false);
        MapPrivateConstructor(map);
    }

    private static void MapJournalEntry(BsonClassMap<JournalEntry> map)
    {
        // The root of the journal's types: the "type" element is always written, also for a single type.
        map.SetIsRootClass(true);
        map.MapIdMember(entry => entry.Id);
        Map(map, entry => entry.OwnerId, "ownerId");
        Map(map, entry => entry.SchemaVersion, "schemaVersion");
        Map(map, entry => entry.RecordedAt, "recordedAt");
        Map(map, entry => entry.OccurredOn, "occurredOn");
        MapOptional(map, entry => entry.Note, "note");
        Map(map, entry => entry.Tags, "tags");
        MapOptional(map, entry => entry.GroupId, "groupId");
        Map(map, entry => entry.Source, "source");
        MapOptional(map, entry => entry.ExternalId, "externalId");
        map.SetIgnoreExtraElements(false);
    }

    // The fields of every single-bucket entry, stored after the common ones (the Deposit shape of step 6).
    private static void MapSingleBucketEntry(BsonClassMap<SingleBucketEntry> map)
    {
        Map(map, entry => entry.BucketId, "bucketId");
        Map(map, entry => entry.Amount, "amount");
        map.SetIgnoreExtraElements(false);
    }

    private static void MapTransfer(BsonClassMap<Transfer> map)
    {
        Map(map, transfer => transfer.FromBucketId, "fromBucketId");
        Map(map, transfer => transfer.ToBucketId, "toBucketId");
        Map(map, transfer => transfer.FromAmount, "fromAmount");
        Map(map, transfer => transfer.ToAmount, "toAmount");
        MapOptional(map, transfer => transfer.Fee, "fee");
        Map(map, transfer => transfer.Rate, "rate");
        MapEntryType(map);
    }

    // A concrete entry type: its stable name and its private constructor.
    private static void MapEntryType<TEntry>(BsonClassMap<TEntry> map)
        where TEntry : JournalEntry
    {
        map.SetDiscriminator(JournalEntryTypes[typeof(TEntry)]);
        map.SetIgnoreExtraElements(false);
        MapPrivateConstructor(map);
    }

    private static void Map<TDocument, TMember>(BsonClassMap<TDocument> map, Expression<Func<TDocument, TMember>> member, string elementName) =>
        map.MapMember(member).SetElementName(elementName).SetIsRequired(true);

    // A field that is optional by design: never written when empty (no "null" in the stored document),
    // and a missing element reads as empty. Only for fields the domain allows to be empty.
    private static void MapOptional<TDocument, TMember>(BsonClassMap<TDocument> map, Expression<Func<TDocument, TMember>> member, string elementName) =>
        map.MapMember(member).SetElementName(elementName).SetIsRequired(false).SetDefaultValue((object?)null).SetIgnoreIfNull(true);

    // The single private constructor, with its parameters matched to the type's properties by name
    // (constructor "timeZone" is property "TimeZone"; inherited properties count). A parameter without a
    // property is a bug; a property that is not mapped fails when the map is frozen.
    private static void MapPrivateConstructor<TDocument>(BsonClassMap<TDocument> map)
    {
        var constructor = typeof(TDocument).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var properties = typeof(TDocument).GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(property => property.Name).ToList();

        var arguments = constructor.GetParameters()
            .Select(parameter => properties.SingleOrDefault(property => string.Equals(property, parameter.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"{typeof(TDocument).Name}: constructor parameter '{parameter.Name}' has no property."))
            .ToArray();

        map.MapConstructor(constructor, arguments);
    }
}
