namespace FinTrack.Shared.Kernel.Persistence;

/// <summary>
/// A document that can be replaced, for example a bucket. Replaced only when the stored
/// <see cref="Version"/> still equals the one that was read (optimistic concurrency).
/// </summary>
public interface IMutableDocument : IDocument
{
    /// <summary>
    /// The version that was read: 0 for a new document that is not stored yet.
    /// The store writes <c>Version + 1</c>; the object itself is never changed by the store.
    /// </summary>
    long Version { get; }

    /// <summary>When the document was first stored (from <c>IClock</c>).</summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>When the document was last stored (from <c>IClock</c>).</summary>
    DateTimeOffset UpdatedAt { get; }
}
