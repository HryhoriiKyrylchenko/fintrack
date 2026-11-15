namespace FinTrack.Shared.Kernel.Ledger;

/// <summary>
/// Marks a command whose handler commits through <see cref="ILedger"/>. The mediator pipeline
/// runs such a handler again (a bounded number of times) when it fails with
/// <c>concurrency.conflict</c>, so the handler must have no side effects before its commit.
/// </summary>
public interface ILedgerCommand;
