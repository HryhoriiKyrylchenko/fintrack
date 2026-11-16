using MongoDB.Bson.Serialization;

namespace FinTrack.Shared.Infrastructure.Mongo.Serialization;

/// <summary>
/// Supplies <see cref="EnumNameSerializer{TEnum}"/> for every enum declared in FinTrack code
/// (nullable ones included), so a new enum needs no registration.
/// </summary>
internal sealed class EnumNameSerializationProvider : IBsonSerializationProvider
{
    public IBsonSerializer? GetSerializer(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (!type.IsEnum || !MongoSerialization.IsOwnType(type))
        {
            return null;
        }

        var serializerType = typeof(EnumNameSerializer<>).MakeGenericType(type);
        return (IBsonSerializer)Activator.CreateInstance(serializerType)!;
    }
}
