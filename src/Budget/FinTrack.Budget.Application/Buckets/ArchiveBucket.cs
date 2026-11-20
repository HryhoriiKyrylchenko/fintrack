using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Shared.Kernel.Time;
using FluentResults;
using Mediator;

namespace FinTrack.Budget.Application.Buckets;

/// <summary>
/// Archives an empty bucket of the user (<c>POST /api/v1/buckets/{id}/archive</c>, ARCHITECTURE 4.1): it frees
/// its name and accepts no new entries. Archiving an archived bucket succeeds and writes nothing. Fails
/// with <see cref="BudgetErrorCodes.SettingsNotFound"/> (404, before the first run: the balance is for today in the
/// owner's time zone, plan 1b B20), <see cref="BudgetErrorCodes.BucketNotFound"/> (404),
/// <see cref="BudgetErrorCodes.BucketSystemBucket"/> or <see cref="BudgetErrorCodes.BucketNotEmpty"/> (422).
/// </summary>
/// <param name="Id">The bucket's ID.</param>
public sealed record ArchiveBucket(Guid Id) : ICommand<Result>, ILedgerCommand;

/// <summary>Reads the state (version first), archives the bucket by its replayed balance and commits it under the version it read.</summary>
public sealed class ArchiveBucketHandler(BudgetStateLoader loader, ILedger ledger, IClock clock) : ICommandHandler<ArchiveBucket, Result>
{
    /// <inheritdoc />
    public async ValueTask<Result> Handle(ArchiveBucket command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var loaded = await loader.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailed)
        {
            return loaded.ToResult();
        }

        var state = loaded.Value;
        if (state.Settings is not { } settings)
        {
            // The state is for today in the owner's time zone (plan 1b B20), which needs the settings.
            return Result.Fail(AppError.NotFound(BudgetErrorCodes.SettingsNotFound));
        }

        if (state.FindBucket(command.Id) is not { } bucket)
        {
            return Result.Fail(AppError.NotFound(BudgetErrorCodes.BucketNotFound));
        }

        if (bucket.Status == BucketStatus.Archived)
        {
            return Result.Ok();
        }

        var archived = bucket.Archive(state.StateAsOf(settings.Today(clock.UtcNow)).BalanceOf(bucket.BucketId));
        if (archived.IsFailed)
        {
            return archived.ToResult();
        }

        var committed = await ledger.CommitAsync(new LedgerCommit(BudgetLedger.Name, state.Version, saves: [archived.Value]), cancellationToken)
            .ConfigureAwait(false);
        return committed.ToResult();
    }
}
