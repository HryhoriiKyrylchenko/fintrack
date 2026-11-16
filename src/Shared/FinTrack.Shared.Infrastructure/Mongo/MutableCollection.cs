using FinTrack.Shared.Kernel.Persistence;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Shared.Kernel.Time;
using FluentResults;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.Mongo;

/// <summary>
/// A collection of documents that are replaced under optimistic concurrency, for example buckets.
/// </summary>
/// <remarks>
/// <para>
/// A new document has <see cref="IMutableDocument.Version"/> 0 and is stored as version 1, with
/// <c>CreatedAt</c> and <c>UpdatedAt</c> from <see cref="IClock"/>. A replace or delete names the
/// version that was read; the write's own filter checks it, so the check and the write are one
/// atomic step on the server (MongoDB has no <c>SaveChanges</c> concurrency token as EF Core does).
/// A replace stores version + 1 and a new <c>UpdatedAt</c>.
/// </para>
/// <para>
/// The document objects themselves are never changed: the stored values differ from the object
/// only in the fields above. Read the document again to see them.
/// </para>
/// </remarks>
/// <typeparam name="TDocument">The stored type.</typeparam>
public sealed class MutableCollection<TDocument> : OwnerScopedCollection<TDocument>
    where TDocument : class, IMutableDocument
{
    private const long FirstVersion = 1;

    private readonly IClock clock;

    /// <summary>Opens the collection <paramref name="collectionName"/> for the owner in <paramref name="ownerContext"/>.</summary>
    public MutableCollection(IMongoDatabase database, string collectionName, IOwnerContext ownerContext, IClock clock)
        : base(database, collectionName, ownerContext)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.clock = clock;
    }

    /// <summary>
    /// Replaces the stored document if its version still equals <paramref name="document"/>'s
    /// <see cref="IMutableDocument.Version"/>. Fails with <c>Conflict</c>
    /// <see cref="ErrorCodes.ConcurrencyConflict"/> when it does not, or when the current owner has
    /// no such document; then nothing is written.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document belongs to another owner (a bug).</exception>
    /// <exception cref="ArgumentException">The version is 0: the document was never read (a bug).</exception>
    public async Task<Result> ReplaceAsync(TDocument document, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        EnsureCurrentOwner(document);
        EnsureStoredVersion(document.Version, nameof(document));

        var replacement = ToBson(document);
        replacement[Elements.Version] = Elements.VersionValue(document.Version + 1);
        replacement[Elements.UpdatedAt] = Elements.InstantValue(clock.UtcNow);

        var filter = VersionFilter(document.Id, document.Version);
        var result = session is null
            ? await Raw.ReplaceOneAsync(filter, replacement, options: (ReplaceOptions?)null, cancellationToken).ConfigureAwait(false)
            : await Raw.ReplaceOneAsync(session, filter, replacement, options: (ReplaceOptions?)null, cancellationToken).ConfigureAwait(false);

        return result.MatchedCount == 1 ? Result.Ok() : Conflict();
    }

    /// <summary>
    /// Deletes the current owner's document <paramref name="id"/> if its version still equals
    /// <paramref name="expectedVersion"/>. Fails with <c>Conflict</c>
    /// <see cref="ErrorCodes.ConcurrencyConflict"/> otherwise; then nothing is deleted.
    /// </summary>
    /// <remarks>Most documents are archived, not deleted (ARCHITECTURE 4.1); delete only what the spec allows.</remarks>
    public async Task<Result> DeleteAsync(Guid id, long expectedVersion, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        EnsureStoredVersion(expectedVersion, nameof(expectedVersion));

        var filter = VersionFilter(id, expectedVersion);
        var result = session is null
            ? await Raw.DeleteOneAsync(filter, cancellationToken).ConfigureAwait(false)
            : await Raw.DeleteOneAsync(session, filter, options: null, cancellationToken).ConfigureAwait(false);

        return result.DeletedCount == 1 ? Result.Ok() : Conflict();
    }

    private protected override void PrepareInsert(TDocument document, BsonDocument stored)
    {
        if (document.Version != 0)
        {
            throw new ArgumentException("A new document has version 0; a stored one is replaced, not inserted.", nameof(document));
        }

        var now = Elements.InstantValue(clock.UtcNow);
        stored[Elements.Version] = Elements.VersionValue(FirstVersion);
        stored[Elements.CreatedAt] = now;
        stored[Elements.UpdatedAt] = now;
    }

    private BsonDocument VersionFilter(Guid id, long version) => new()
    {
        { Elements.Id, Elements.IdValue(id) },
        { Elements.OwnerId, CurrentOwnerValue() },
        { Elements.Version, Elements.VersionValue(version) },
    };

    private static void EnsureStoredVersion(long version, string parameterName)
    {
        if (version < FirstVersion)
        {
            throw new ArgumentException("A stored document has version 1 or higher; read it before changing it.", parameterName);
        }
    }
}
