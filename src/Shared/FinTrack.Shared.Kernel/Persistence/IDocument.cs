using FinTrack.Shared.Kernel.Ids;

namespace FinTrack.Shared.Kernel.Persistence;

/// <summary>
/// Something stored as one MongoDB document. Domain types implement this directly; the
/// MongoDB mapping lives in Infrastructure, so Domain stays free of database code.
/// </summary>
/// <remarks>
/// Every document has these three fields (CLAUDE.md rule 9). A document is either
/// <see cref="IAppendOnlyDocument"/> (never changed after insert) or
/// <see cref="IMutableDocument"/> (replaced under optimistic concurrency).
/// </remarks>
public interface IDocument
{
    /// <summary>Client-generated version 7 UUID; unique within its collection.</summary>
    Guid Id { get; }

    /// <summary>The owner. Every query and write is filtered by it.</summary>
    OwnerId OwnerId { get; }

    /// <summary>
    /// Version of the document's shape. Raised when the stored format changes; older
    /// documents are upgraded on read (ARCHITECTURE 2.3, schema evolution).
    /// </summary>
    int SchemaVersion { get; }
}
