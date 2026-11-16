using FinTrack.Shared.Kernel.Ids;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace FinTrack.Shared.Infrastructure.Mongo.Serialization;

/// <summary>
/// Stores a strongly typed ID exactly like the <see cref="Guid"/> it wraps: a standard UUID
/// (binary subtype 4). The wrapper type is not stored, so it can be renamed freely.
/// </summary>
internal sealed class StronglyTypedIdSerializer<TId> : StructSerializerBase<TId>
    where TId : struct, IStronglyTypedId<TId>
{
    private static readonly GuidSerializer GuidValueSerializer = new(GuidRepresentation.Standard);

    public override TId Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var value = GuidValueSerializer.Deserialize(context, args);
        return value == Guid.Empty
            ? throw new FormatException($"A stored {typeof(TId).Name} cannot be empty.")
            : TId.From(value);
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, TId value) =>
        GuidValueSerializer.Serialize(context, args, value.Value);
}
