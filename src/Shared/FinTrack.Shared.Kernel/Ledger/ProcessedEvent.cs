namespace FinTrack.Shared.Kernel.Ledger;

/// <summary>
/// Records that one handler handled one event. Stored in the same transaction as the
/// handler's own writes, so a second delivery of the event is recognised and changes nothing.
/// </summary>
public sealed record ProcessedEvent
{
    /// <summary>Creates the record; throws <see cref="ArgumentException"/> for invalid values (a bug).</summary>
    /// <param name="handlerName">Stable name of the handler, for example <c>budget.on_cash_out</c>. Never the CLR type name.</param>
    /// <param name="eventId">The handled event's <c>EventId</c>.</param>
    public ProcessedEvent(string handlerName, Guid eventId)
    {
        if (!StableName.IsValid(handlerName))
        {
            throw new ArgumentException($"Handler name '{handlerName}' must be a stable name, for example 'budget.on_cash_out'.", nameof(handlerName));
        }

        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("An event identifier cannot be empty.", nameof(eventId));
        }

        HandlerName = handlerName;
        EventId = eventId;
    }

    /// <summary>Stable name of the handler.</summary>
    public string HandlerName { get; }

    /// <summary>The handled event.</summary>
    public Guid EventId { get; }
}
