using FinTrack.Shared.Kernel.Ledger;
using FluentResults;

namespace FinTrack.Shared.Infrastructure.Outbox;

/// <summary>
/// The <see cref="ILedger"/> the application uses: commits through the inner ledger, then delivers
/// the committed events in-process right away (ARCHITECTURE 2.3). Delivery failures never change
/// the commit's result; the sweep retries them.
/// </summary>
public sealed class DispatchingLedger(ILedger inner, OutboxDispatcher dispatcher) : ILedger
{
    /// <inheritdoc />
    public Task<long> GetVersionAsync(LedgerName ledger, CancellationToken cancellationToken) =>
        inner.GetVersionAsync(ledger, cancellationToken);

    /// <inheritdoc />
    public async Task<Result<LedgerCommitted>> CommitAsync(LedgerCommit commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);

        var result = await inner.CommitAsync(commit, cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess && !result.Value.AlreadyHandled && commit.Events.Count > 0)
        {
            await dispatcher.DispatchAsync(
                commit.Ledger,
                [.. commit.Events.Select(integrationEvent => integrationEvent.EventId)],
                cancellationToken).ConfigureAwait(false);
        }

        return result;
    }
}
