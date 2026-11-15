using FinTrack.Shared.Kernel.Persistence;
using FluentResults;

namespace FinTrack.Shared.Kernel.Ledger;

/// <summary>
/// The write protocol of a context: every change for one owner and context goes through here,
/// checked against the ledger version (CLAUDE.md rule 7). The owner is always the one from
/// <see cref="IOwnerContext"/>.
/// </summary>
/// <remarks>
/// <para>A command handler:</para>
/// <list type="number">
/// <item><description>reads the version with <see cref="GetVersionAsync"/>;</description></item>
/// <item><description>then loads the journal and builds state (version first: if a write lands in between, the commit below fails instead of passing on stale data);</description></item>
/// <item><description>checks the business rules;</description></item>
/// <item><description>calls <see cref="CommitAsync"/> with the version it read.</description></item>
/// </list>
/// <para>
/// On <c>concurrency.conflict</c> the whole handler runs again (commands marked
/// <see cref="ILedgerCommand"/>), so a handler must have no side effects before the commit.
/// </para>
/// </remarks>
public interface ILedger
{
    /// <summary>
    /// The current version of the owner's ledger in <paramref name="ledger"/>; 0 when nothing has
    /// been written yet. Read it before loading the journal.
    /// </summary>
    Task<long> GetVersionAsync(LedgerName ledger, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the commit in one transaction and raises the version by one: inserts the appends,
    /// inserts or replaces the saves, stores the events in the outbox and records the handled event.
    /// </summary>
    /// <returns>
    /// The new version; or, when <see cref="LedgerCommit.HandledEvent"/> was already handled, the
    /// current version with <see cref="LedgerCommitted.AlreadyHandled"/> set and nothing written.
    /// Fails with a <c>Conflict</c> error <c>concurrency.conflict</c> when the version is no longer
    /// <see cref="LedgerCommit.ExpectedVersion"/> or a saved document's stored version has changed;
    /// then nothing is written.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// A document or event belongs to another owner than <see cref="IOwnerContext.OwnerId"/> (a bug).
    /// </exception>
    Task<Result<LedgerCommitted>> CommitAsync(LedgerCommit commit, CancellationToken cancellationToken);
}
