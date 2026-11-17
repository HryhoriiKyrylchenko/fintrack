namespace FinTrack.Shared.Infrastructure.Mongo;

/// <summary>
/// A document of reference data: public data that is the same for every owner, such as FX rates
/// (ARCHITECTURE 2.2, plan 1a D8). It has no <c>OwnerId</c> and is stored only through
/// <see cref="ReferenceCollection{TDocument}"/>; a type that is also an owner document is refused.
/// </summary>
public interface IReferenceDocument
{
    /// <summary>Server-generated version 7 UUID, stored as <c>_id</c>.</summary>
    Guid Id { get; }

    /// <summary>Version of the document's shape (ARCHITECTURE 2.3, schema evolution).</summary>
    int SchemaVersion { get; }

    /// <summary>
    /// Registers the type's explicit storage mapping. <see cref="ReferenceCollection{TDocument}"/>
    /// calls it before the collection is opened, because the driver fixes a type's serializer then.
    /// Must be safe to call more than once and from several threads.
    /// </summary>
    static abstract void RegisterMapping();
}
