using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Results;
using FluentResults;
using Mediator;

namespace FinTrack.Shared.Infrastructure.Messaging;

/// <summary>
/// Runs a ledger command's handler again when it fails with <see cref="ErrorCodes.ConcurrencyConflict"/>:
/// another write landed between reading the ledger version and committing (CLAUDE.md rule 7).
/// The handler then reloads the journal and decides again on fresh state.
/// </summary>
/// <remarks>
/// Only <see cref="ILedgerCommand"/>s are retried, at most <see cref="MaxAttempts"/> times in total;
/// after that the conflict is returned. Any other failure, and any exception, is returned at once.
/// A retry is safe because a ledger handler has no side effects before its commit.
/// </remarks>
/// <typeparam name="TMessage">The ledger command.</typeparam>
/// <typeparam name="TResponse"><see cref="Result"/> or <see cref="Result{TValue}"/>.</typeparam>
public sealed class RetryOnConflictBehavior<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage, ILedgerCommand
    where TResponse : ResultBase<TResponse>, new()
{
    /// <summary>How many times the handler runs at most, the first run included.</summary>
    public const int MaxAttempts = 3;

    /// <inheritdoc />
    public async ValueTask<TResponse> Handle(TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        var attempt = 1;
        while (true)
        {
            var result = await next(message, cancellationToken).ConfigureAwait(false);

            if (attempt == MaxAttempts || !IsConcurrencyConflict(result))
            {
                return result;
            }

            attempt++;
        }
    }

    private static bool IsConcurrencyConflict(TResponse result) =>
        result.IsFailed
        && result.Errors.All(error => error is AppError { Kind: ErrorKind.Conflict, Code: ErrorCodes.ConcurrencyConflict });
}
