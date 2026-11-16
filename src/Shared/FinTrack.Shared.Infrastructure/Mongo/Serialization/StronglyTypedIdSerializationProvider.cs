using FinTrack.Shared.Kernel.Ids;
using MongoDB.Bson.Serialization;

namespace FinTrack.Shared.Infrastructure.Mongo.Serialization;

/// <summary>
/// Supplies <see cref="StronglyTypedIdSerializer{TId}"/> for every type that implements
/// <see cref="IStronglyTypedId{TSelf}"/>, so a new identifier type needs no registration.
/// </summary>
internal sealed class StronglyTypedIdSerializationProvider : IBsonSerializationProvider
{
    public IBsonSerializer? GetSerializer(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var isStronglyTypedId = type.IsValueType && type.GetInterfaces().Any(implemented =>
            implemented.IsGenericType
            && implemented.GetGenericTypeDefinition() == typeof(IStronglyTypedId<>)
            && implemented.GenericTypeArguments[0] == type);

        if (!isStronglyTypedId)
        {
            return null;
        }

        var serializerType = typeof(StronglyTypedIdSerializer<>).MakeGenericType(type);
        return (IBsonSerializer)Activator.CreateInstance(serializerType)!;
    }
}
