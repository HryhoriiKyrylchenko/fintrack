using FinTrack.Shared.Kernel.Ledger;

namespace FinTrack.Shared.Infrastructure.Ledger;

/// <summary>
/// All registered ledgers and their document collections, plus the names of each ledger's own
/// collections (ARCHITECTURE 2.2).
/// </summary>
public sealed class LedgerCollections
{
    private readonly LedgerDocuments[] ledgers;

    /// <summary>Combines the registrations of all contexts; a ledger or a document type may appear only once.</summary>
    public LedgerCollections(IEnumerable<LedgerDocuments> ledgers)
    {
        ArgumentNullException.ThrowIfNull(ledgers);

        this.ledgers = [.. ledgers];

        if (this.ledgers.Select(documents => documents.Ledger).Distinct().Count() != this.ledgers.Length)
        {
            throw new ArgumentException("A ledger is registered more than once.", nameof(ledgers));
        }

        var types = this.ledgers.SelectMany(documents => documents.Mappings).Select(mapping => mapping.DocumentType).ToList();
        if (types.Distinct().Count() != types.Count)
        {
            throw new ArgumentException("A document type is mapped in more than one ledger.", nameof(ledgers));
        }
    }

    /// <summary>The registered ledgers.</summary>
    public IEnumerable<LedgerName> Ledgers => ledgers.Select(documents => documents.Ledger);

    /// <summary>Every mapped document type of every ledger.</summary>
    public IEnumerable<LedgerDocumentMapping> Mappings => ledgers.SelectMany(documents => documents.Mappings);

    /// <summary>Holds each owner's <c>LedgerHead</c> of <paramref name="ledger"/>.</summary>
    public static string HeadsCollection(LedgerName ledger) => $"{ledger.Value}_ledger_heads";

    /// <summary>Holds the outbox messages of <paramref name="ledger"/>.</summary>
    public static string OutboxCollection(LedgerName ledger) => $"{ledger.Value}_outbox";

    /// <summary>Holds the records of events handled by <paramref name="ledger"/>'s handlers.</summary>
    public static string ProcessedEventsCollection(LedgerName ledger) => $"{ledger.Value}_processed_events";

    /// <summary>Whether <paramref name="ledger"/> is registered.</summary>
    public bool Contains(LedgerName ledger) => ledgers.Any(documents => documents.Ledger == ledger);

    /// <summary>
    /// The mapping for <paramref name="documentType"/>: its own type, otherwise the one registered base
    /// type it derives from; <c>null</c> when none matches.
    /// </summary>
    public LedgerDocumentMapping? Find(Type documentType)
    {
        ArgumentNullException.ThrowIfNull(documentType);

        var matches = Mappings.Where(mapping => mapping.DocumentType.IsAssignableFrom(documentType)).ToList();
        return matches.Find(mapping => mapping.DocumentType == documentType)
            ?? (matches.Count <= 1
                ? matches.SingleOrDefault()
                : throw new InvalidOperationException($"{documentType.Name} derives from more than one mapped type."));
    }
}
