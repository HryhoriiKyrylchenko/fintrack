using FluentResults;
using Mediator;

namespace FinTrack.Shared.Infrastructure.Outbox;

/// <summary>Delivers every pending outbox message (the sweep timer's command).</summary>
public sealed record DispatchPendingOutbox : ICommand<Result<int>>;

/// <summary>Runs <see cref="OutboxDispatcher.DispatchPendingAsync"/>; the result is the number of delivered messages.</summary>
public sealed class DispatchPendingOutboxHandler(OutboxDispatcher dispatcher) : ICommandHandler<DispatchPendingOutbox, Result<int>>
{
    /// <inheritdoc />
    public async ValueTask<Result<int>> Handle(DispatchPendingOutbox command, CancellationToken cancellationToken) =>
        Result.Ok(await dispatcher.DispatchPendingAsync(cancellationToken).ConfigureAwait(false));
}
