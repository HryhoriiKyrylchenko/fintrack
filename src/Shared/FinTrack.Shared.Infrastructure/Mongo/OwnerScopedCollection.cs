using System.Linq.Expressions;
using FinTrack.Shared.Kernel.Persistence;
using FinTrack.Shared.Kernel.Results;
using FluentResults;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.Mongo;

/// <summary>
/// A collection of one document type in which every read and write is limited to the current
/// owner from <see cref="IOwnerContext"/> (CLAUDE.md rule 9, invariant 12). The underlying
/// MongoDB collection is never exposed, so the owner filter cannot be forgotten.
/// </summary>
/// <remarks>
/// <para>
/// Every filter is combined with <c>ownerId</c>; a document of another owner behaves as if it did
/// not exist. Writing a document that belongs to another owner is a bug and throws.
/// </para>
/// <para>
/// Use <see cref="AppendOnlyCollection{TDocument}"/> for journal-style documents (insert only) and
/// <see cref="MutableCollection{TDocument}"/> for documents replaced under optimistic concurrency.
/// Every method accepts an optional session, so it can take part in a transaction.
/// </para>
/// </remarks>
/// <typeparam name="TDocument">The stored type.</typeparam>
public abstract class OwnerScopedCollection<TDocument>
    where TDocument : class, IDocument
{
    private readonly IOwnerContext ownerContext;

    private protected OwnerScopedCollection(IMongoDatabase database, string collectionName, IOwnerContext ownerContext)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentNullException.ThrowIfNull(ownerContext);

        // Reference data has no owner; owner data never goes into its collections (plan 1a D8).
        if (collectionName.StartsWith(ReferenceCollection.NamePrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Owner data is never stored in a reference-data collection.", nameof(collectionName));
        }

        // The format must be known before the first serializer of TDocument is looked up.
        MongoSerialization.Register();

        this.ownerContext = ownerContext;
        Typed = database.GetCollection<TDocument>(collectionName);
        Raw = database.GetCollection<BsonDocument>(collectionName);
        Elements = DocumentElements.Of<TDocument>();
    }

    /// <summary>The typed collection; reads only, always through <see cref="OwnerFilter"/>.</summary>
    private protected IMongoCollection<TDocument> Typed { get; }

    /// <summary>The same collection as BSON, for writes whose stored fields differ from the object.</summary>
    private protected IMongoCollection<BsonDocument> Raw { get; }

    /// <summary>Where the document fields are stored.</summary>
    private protected DocumentElements Elements { get; }

    /// <summary>The current owner's document with <paramref name="id"/>, or <c>null</c>.</summary>
    public async Task<TDocument?> GetAsync(Guid id, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        var filter = new BsonDocumentFilterDefinition<TDocument>(new BsonDocument(Elements.Id, Elements.IdValue(id)));
        var found = await FindAsync(filter, sort: null, session, cancellationToken).ConfigureAwait(false);
        return found.SingleOrDefault();
    }

    /// <summary>The current owner's documents that match <paramref name="filter"/>.</summary>
    public Task<IReadOnlyList<TDocument>> FindAsync(
        Expression<Func<TDocument, bool>> filter,
        SortDefinition<TDocument>? sort = null,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return FindAsync(Builders<TDocument>.Filter.Where(filter), sort, session, cancellationToken);
    }

    /// <summary>The current owner's documents that match <paramref name="filter"/>.</summary>
    public async Task<IReadOnlyList<TDocument>> FindAsync(
        FilterDefinition<TDocument> filter,
        SortDefinition<TDocument>? sort = null,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var scoped = Builders<TDocument>.Filter.And(OwnerFilter(), filter);
        var find = session is null ? Typed.Find(scoped) : Typed.Find(session, scoped);
        if (sort is not null)
        {
            find = find.Sort(sort);
        }

        return await find.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts a new document. Fails with <c>Conflict</c> <see cref="ErrorCodes.ConcurrencyConflict"/>
    /// when a document with the same <c>Id</c> already exists (of any owner).
    /// </summary>
    /// <exception cref="InvalidOperationException">The document belongs to another owner (a bug).</exception>
    /// <exception cref="ArgumentException">The document is invalid for insert (a bug).</exception>
    public async Task<Result> InsertAsync(TDocument document, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        EnsureCurrentOwner(document);

        if (document.Id == Guid.Empty)
        {
            throw new ArgumentException("A document needs an identifier.", nameof(document));
        }

        var stored = ToBson(document);
        PrepareInsert(document, stored);

        try
        {
            if (session is null)
            {
                await Raw.InsertOneAsync(stored, options: null, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Raw.InsertOneAsync(session, stored, options: null, cancellationToken).ConfigureAwait(false);
            }

            return Result.Ok();
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return Conflict();
        }
    }

    /// <summary>Changes the stored form of a new document before insert (for example its version).</summary>
    private protected virtual void PrepareInsert(TDocument document, BsonDocument stored)
    {
    }

    /// <summary>The filter on the current owner; part of every query and write.</summary>
    private protected FilterDefinition<TDocument> OwnerFilter() =>
        new BsonDocumentFilterDefinition<TDocument>(new BsonDocument(Elements.OwnerId, Elements.OwnerValue(ownerContext.OwnerId)));

    /// <summary>The current owner's value, for raw BSON filters.</summary>
    private protected BsonValue CurrentOwnerValue() => Elements.OwnerValue(ownerContext.OwnerId);

    private protected void EnsureCurrentOwner(TDocument document)
    {
        if (document.OwnerId != ownerContext.OwnerId)
        {
            throw new InvalidOperationException("The document belongs to another owner than the current one.");
        }
    }

    private protected static Result Conflict() => Result.Fail(AppError.Conflict(ErrorCodes.ConcurrencyConflict));

    private protected static BsonDocument ToBson(TDocument document) =>
        document.ToBsonDocument(BsonSerializer.LookupSerializer<TDocument>());
}
