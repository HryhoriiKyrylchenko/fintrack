using FinTrack.Shared.Kernel.Events;
using FinTrack.Shared.Kernel.Persistence;

namespace FinTrack.Shared.Kernel.Ledger;

/// <summary>
/// Everything one command writes to one context, committed by <see cref="ILedger.CommitAsync"/>
/// in one transaction together with the ledger version increase (CLAUDE.md rule 7).
/// </summary>
/// <remarks>
/// Invalid content (nothing to write, a negative version, a null item, a document or event
/// twice) is a bug and throws <see cref="ArgumentException"/>.
/// </remarks>
public sealed record LedgerCommit
{
    /// <summary>Creates a commit. The lists are copied, so later changes to them have no effect.</summary>
    /// <param name="ledger">The context's ledger.</param>
    /// <param name="expectedVersion">The version from <see cref="ILedger.GetVersionAsync"/>, read before the journal.</param>
    /// <param name="appends">Append-only documents to insert, typically journal entries.</param>
    /// <param name="saves">Mutable documents to insert (version 0) or replace (stored version must match).</param>
    /// <param name="events">Integration events for the outbox.</param>
    /// <param name="handledEvent">Set by an event handler: the event it handled.</param>
    public LedgerCommit(
        LedgerName ledger,
        long expectedVersion,
        IReadOnlyList<IAppendOnlyDocument>? appends = null,
        IReadOnlyList<IMutableDocument>? saves = null,
        IReadOnlyList<IIntegrationEvent>? events = null,
        ProcessedEvent? handledEvent = null)
    {
        if (ledger.Value is null)
        {
            throw new ArgumentException("The ledger name is missing.", nameof(ledger));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);

        Ledger = ledger;
        ExpectedVersion = expectedVersion;
        Appends = CopyWithoutNulls(appends, nameof(appends));
        Saves = CopyWithoutNulls(saves, nameof(saves));
        Events = CopyWithoutNulls(events, nameof(events));
        HandledEvent = handledEvent;

        if (Appends.Count == 0 && Saves.Count == 0 && Events.Count == 0 && HandledEvent is null)
        {
            throw new ArgumentException("A commit must write something.", nameof(appends));
        }

        var documentIds = Appends.Select(document => document.Id).Concat(Saves.Select(document => document.Id)).ToList();
        if (documentIds.Distinct().Count() != documentIds.Count)
        {
            throw new ArgumentException("A document appears twice in one commit.", nameof(appends));
        }

        if (Events.Select(integrationEvent => integrationEvent.EventId).Distinct().Count() != Events.Count)
        {
            throw new ArgumentException("An event appears twice in one commit.", nameof(events));
        }
    }

    /// <summary>The context's ledger.</summary>
    public LedgerName Ledger { get; }

    /// <summary>The ledger version the command's checks were based on.</summary>
    public long ExpectedVersion { get; }

    /// <summary>Append-only documents to insert.</summary>
    public IReadOnlyList<IAppendOnlyDocument> Appends { get; }

    /// <summary>Mutable documents to insert or replace.</summary>
    public IReadOnlyList<IMutableDocument> Saves { get; }

    /// <summary>Integration events to store in the outbox.</summary>
    public IReadOnlyList<IIntegrationEvent> Events { get; }

    /// <summary>The event an event handler handled, or <c>null</c> for a command.</summary>
    public ProcessedEvent? HandledEvent { get; }

    private static T[] CopyWithoutNulls<T>(IReadOnlyList<T>? items, string parameterName)
        where T : class
    {
        if (items is null)
        {
            return [];
        }

        if (items.Any(item => item is null))
        {
            throw new ArgumentException("A commit cannot contain null items.", parameterName);
        }

        return [.. items];
    }
}
