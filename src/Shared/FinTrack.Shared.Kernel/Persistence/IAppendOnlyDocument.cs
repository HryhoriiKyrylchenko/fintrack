namespace FinTrack.Shared.Kernel.Persistence;

/// <summary>
/// A document that is inserted once and never updated or deleted: journal entries,
/// outbox messages, processed-event records. Corrections are new documents (reversals).
/// </summary>
public interface IAppendOnlyDocument : IDocument
{
    /// <summary>When the system recorded it (from <c>IClock</c>), not the business date.</summary>
    DateTimeOffset RecordedAt { get; }
}
