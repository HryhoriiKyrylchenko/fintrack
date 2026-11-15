namespace FinTrack.Shared.Kernel.Ledger;

/// <summary>The outcome of a successful <see cref="ILedger.CommitAsync"/>.</summary>
/// <param name="Version">The ledger version after the commit.</param>
/// <param name="AlreadyHandled">
/// True when the commit's <see cref="LedgerCommit.HandledEvent"/> had been handled before:
/// nothing was written and <see cref="Version"/> is the unchanged current version.
/// </param>
public sealed record LedgerCommitted(long Version, bool AlreadyHandled);
