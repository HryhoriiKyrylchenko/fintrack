using System.Text.RegularExpressions;
using FinTrack.Shared.Infrastructure.Mongo;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Persistence;
using FinTrack.Shared.Kernel.Time;
using FluentResults;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.Ledger;

/// <summary>
/// Which collection each document type of one context's ledger is stored in. Each context
/// registers one with <see cref="LedgerServiceCollectionExtensions.AddLedgerDocuments"/>.
/// </summary>
/// <remarks>
/// A document is stored in the collection of its own type or of a registered base type, so
/// subclasses (for example the entry types of one journal) can share a collection. Collection
/// names start with the ledger name and an underscore (<c>budget_journal</c>); the ledger's own
/// collections (<c>_ledger_heads</c>, <c>_outbox</c>, <c>_processed_events</c>) are reserved.
/// </remarks>
public sealed partial class LedgerDocuments
{
    private readonly List<LedgerDocumentMapping> mappings = [];

    /// <summary>Starts the mapping of <paramref name="ledger"/>.</summary>
    public LedgerDocuments(LedgerName ledger)
    {
        if (ledger.Value is null)
        {
            throw new ArgumentException("The ledger name is missing.", nameof(ledger));
        }

        Ledger = ledger;
    }

    /// <summary>The context's ledger.</summary>
    public LedgerName Ledger { get; }

    /// <summary>The mapped document types.</summary>
    public IReadOnlyList<LedgerDocumentMapping> Mappings => mappings;

    /// <summary>Stores <typeparamref name="TDocument"/> (and its subclasses) in an append-only collection.</summary>
    public LedgerDocuments AppendOnly<TDocument>(string collectionName)
        where TDocument : class, IAppendOnlyDocument =>
        Add(typeof(TDocument), collectionName, (database, owner, clock, document, session, cancellationToken) =>
            new AppendOnlyCollection<TDocument>(database, collectionName, owner)
                .InsertAsync((TDocument)document, session, cancellationToken));

    /// <summary>
    /// Stores <typeparamref name="TDocument"/> (and its subclasses) in a mutable collection: version 0
    /// is inserted, any other version replaces the stored document if its version still matches.
    /// </summary>
    public LedgerDocuments Mutable<TDocument>(string collectionName)
        where TDocument : class, IMutableDocument =>
        Add(typeof(TDocument), collectionName, (database, owner, clock, document, session, cancellationToken) =>
        {
            var collection = new MutableCollection<TDocument>(database, collectionName, owner, clock);
            var typed = (TDocument)document;
            return typed.Version == 0
                ? collection.InsertAsync(typed, session, cancellationToken)
                : collection.ReplaceAsync(typed, session, cancellationToken);
        });

    private LedgerDocuments Add(Type documentType, string collectionName, LedgerDocumentWriter write)
    {
        if (!IsValidCollectionName(collectionName))
        {
            throw new ArgumentException(
                $"Collection '{collectionName}' must be '{Ledger.Value}_' followed by lower-case letters, digits or underscores, and not a reserved ledger collection.",
                nameof(collectionName));
        }

        if (mappings.Any(mapping => mapping.DocumentType == documentType))
        {
            throw new ArgumentException($"{documentType.Name} is already mapped in ledger '{Ledger.Value}'.", nameof(collectionName));
        }

        if (mappings.Any(mapping => mapping.CollectionName == collectionName))
        {
            throw new ArgumentException($"Collection '{collectionName}' is already used by another type.", nameof(collectionName));
        }

        mappings.Add(new LedgerDocumentMapping(Ledger, documentType, collectionName, write));
        return this;
    }

    private bool IsValidCollectionName(string? collectionName) =>
        collectionName is not null
        && collectionName.StartsWith(Ledger.Value + "_", StringComparison.Ordinal)
        && NameFormat().IsMatch(collectionName)
        && collectionName != LedgerCollections.HeadsCollection(Ledger)
        && collectionName != LedgerCollections.OutboxCollection(Ledger)
        && collectionName != LedgerCollections.ProcessedEventsCollection(Ledger);

    [GeneratedRegex("^[a-z][a-z0-9]*_[a-z0-9_]+\\z")]
    private static partial Regex NameFormat();
}

/// <summary>Writes one document of a mapped type inside the ledger's transaction.</summary>
internal delegate Task<Result> LedgerDocumentWriter(
    IMongoDatabase database,
    IOwnerContext owner,
    IClock clock,
    IDocument document,
    IClientSessionHandle session,
    CancellationToken cancellationToken);

/// <summary>One mapped document type.</summary>
public sealed class LedgerDocumentMapping
{
    internal LedgerDocumentMapping(LedgerName ledger, Type documentType, string collectionName, LedgerDocumentWriter write)
    {
        Ledger = ledger;
        DocumentType = documentType;
        CollectionName = collectionName;
        Write = write;
    }

    /// <summary>The ledger the type belongs to.</summary>
    public LedgerName Ledger { get; }

    /// <summary>The mapped type; subclasses use the same collection.</summary>
    public Type DocumentType { get; }

    /// <summary>The collection, for example <c>budget_journal</c>.</summary>
    public string CollectionName { get; }

    internal LedgerDocumentWriter Write { get; }
}
