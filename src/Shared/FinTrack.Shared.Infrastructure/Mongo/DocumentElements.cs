using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Persistence;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace FinTrack.Shared.Infrastructure.Mongo;

/// <summary>
/// The stored names and serializers of the fields every document has, taken from the document's
/// own serializer, so raw filters and stamped values use exactly the stored format.
/// </summary>
internal sealed class DocumentElements
{
    private readonly BsonSerializationInfo id;
    private readonly BsonSerializationInfo owner;
    private readonly BsonSerializationInfo? version;
    private readonly BsonSerializationInfo? createdAt;
    private readonly BsonSerializationInfo? updatedAt;

    private DocumentElements(Type documentType, IBsonDocumentSerializer serializer)
    {
        id = Member(documentType, serializer, nameof(IDocument.Id));
        owner = Member(documentType, serializer, nameof(IDocument.OwnerId));

        if (typeof(IMutableDocument).IsAssignableFrom(documentType))
        {
            version = Member(documentType, serializer, nameof(IMutableDocument.Version));
            createdAt = Member(documentType, serializer, nameof(IMutableDocument.CreatedAt));
            updatedAt = Member(documentType, serializer, nameof(IMutableDocument.UpdatedAt));
        }
    }

    public string Id => id.ElementName;

    public string OwnerId => owner.ElementName;

    public string Version => MutableOnly(version).ElementName;

    public string CreatedAt => MutableOnly(createdAt).ElementName;

    public string UpdatedAt => MutableOnly(updatedAt).ElementName;

    public static DocumentElements Of<TDocument>()
        where TDocument : IDocument =>
        BsonSerializer.LookupSerializer<TDocument>() is IBsonDocumentSerializer serializer
            ? new DocumentElements(typeof(TDocument), serializer)
            : throw new InvalidOperationException($"{typeof(TDocument).Name} is not stored as a document.");

    public BsonValue IdValue(Guid value) => id.SerializeValue(value);

    public BsonValue OwnerValue(OwnerId value) => owner.SerializeValue(value);

    public BsonValue VersionValue(long value) => MutableOnly(version).SerializeValue(value);

    public BsonValue InstantValue(DateTimeOffset value) => MutableOnly(updatedAt).SerializeValue(value);

    private static BsonSerializationInfo Member(Type documentType, IBsonDocumentSerializer serializer, string memberName) =>
        serializer.TryGetMemberSerializationInfo(memberName, out var info)
            ? info
            : throw new InvalidOperationException($"{documentType.Name} does not store its {memberName}.");

    private static BsonSerializationInfo MutableOnly(BsonSerializationInfo? info) =>
        info ?? throw new InvalidOperationException("Only mutable documents have a version and timestamps.");
}
