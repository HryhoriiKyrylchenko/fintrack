using FinTrack.Budget.Domain;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Results;
using FluentResults;
using FluentValidation;
using Mediator;

namespace FinTrack.Budget.Application.Buckets;

/// <summary>
/// Renames a bucket (<c>POST /api/v1/buckets/{id}/rename</c>). Renaming to the current name succeeds
/// and writes nothing; a change of case only is a rename.
/// </summary>
/// <param name="Id">The bucket's ID.</param>
/// <param name="Name">The new name (<see cref="BucketNameRules"/>).</param>
public sealed record RenameBucket(Guid Id, string Name) : ICommand<Result>, ILedgerCommand;

/// <summary>Shape checks; the rules about which buckets may be renamed are in the domain.</summary>
public sealed class RenameBucketValidator : AbstractValidator<RenameBucket>
{
    /// <summary>Creates the rules.</summary>
    public RenameBucketValidator() => RuleFor(command => command.Name).BucketName();
}

/// <summary>
/// Reads the state (version first), renames the bucket and commits it under the version it read.
/// Fails with <see cref="BudgetErrorCodes.BucketNotFound"/> or the domain's rename rules.
/// </summary>
public sealed class RenameBucketHandler(BudgetStateLoader loader, ILedger ledger) : ICommandHandler<RenameBucket, Result>
{
    /// <inheritdoc />
    public async ValueTask<Result> Handle(RenameBucket command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var state = await loader.LoadAsync(cancellationToken).ConfigureAwait(false);

        if (state.FindBucket(command.Id) is not { } bucket)
        {
            return Result.Fail(AppError.NotFound(BudgetErrorCodes.BucketNotFound));
        }

        if (string.Equals(bucket.Name, command.Name, StringComparison.Ordinal))
        {
            return Result.Ok();
        }

        var renamed = bucket.Rename(command.Name, state.Buckets);
        if (renamed.IsFailed)
        {
            return renamed.ToResult();
        }

        var committed = await ledger.CommitAsync(new LedgerCommit(BudgetLedger.Name, state.Version, saves: [renamed.Value]), cancellationToken)
            .ConfigureAwait(false);
        return committed.ToResult();
    }
}
