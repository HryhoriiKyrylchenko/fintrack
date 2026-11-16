using System.Linq.Expressions;
using System.Reflection;
using FinTrack.Budget.Domain.Settings;
using FinTrack.Shared.Infrastructure.Mongo;
using MongoDB.Bson.Serialization;

namespace FinTrack.Budget.Infrastructure;

/// <summary>
/// How Budget's domain types are stored. The stored format is permanent (ARCHITECTURE 2.2), so
/// every type is mapped explicitly here; Domain has no database attributes.
/// </summary>
/// <remarks>
/// <para>The pattern, reused by every Budget document type:</para>
/// <list type="bullet">
/// <item><description>every stored property is mapped by hand with an explicit element name, so renaming a C# property never changes what is stored (no automatic mapping, no naming convention);</description></item>
/// <item><description>the type's private constructor restores a stored document; its parameters are matched to the mapped properties by name;</description></item>
/// <item><description>unknown elements make reading fail, as for every stored type.</description></item>
/// </list>
/// <para>
/// Value formats (IDs, currencies, instants) come from the shared format in
/// <see cref="MongoSerialization"/>. Like that format, class maps are global to the process and
/// must be registered before the first use; <see cref="BudgetServiceCollectionExtensions.AddBudget"/> does it.
/// </para>
/// </remarks>
public static class BudgetMongoMapping
{
    private static readonly Lock Gate = new();
    private static bool registered;

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

    private static void Map<TDocument, TMember>(BsonClassMap<TDocument> map, Expression<Func<TDocument, TMember>> member, string elementName) =>
        map.MapMember(member).SetElementName(elementName).SetIsRequired(true);

    // The single private constructor, with its parameters matched to the mapped properties by name
    // (constructor "timeZone" is property "TimeZone"). A parameter without a mapped property is a bug.
    private static void MapPrivateConstructor<TDocument>(BsonClassMap<TDocument> map)
    {
        var constructor = typeof(TDocument).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var members = map.DeclaredMemberMaps.Select(member => member.MemberName).ToList();

        var arguments = constructor.GetParameters()
            .Select(parameter => members.SingleOrDefault(member => string.Equals(member, parameter.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"{typeof(TDocument).Name}: constructor parameter '{parameter.Name}' has no mapped property."))
            .ToArray();

        map.MapConstructor(constructor, arguments);
    }
}
