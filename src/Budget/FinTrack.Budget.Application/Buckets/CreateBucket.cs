using FinTrack.Budget.Contracts.V1.Buckets;
using FinTrack.Budget.Domain;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Persistence;
using FinTrack.Shared.Kernel.Results;
using FluentResults;
using FluentValidation;
using Mediator;
using Bucket = FinTrack.Budget.Domain.Buckets.Bucket;
using BucketId = FinTrack.Budget.Domain.Buckets.BucketId;

namespace FinTrack.Budget.Application.Buckets;

/// <summary>
/// Creates a bucket of the user (<c>POST /api/v1/buckets</c>). The client generates <paramref name="Id"/>;
/// a repeat with the same ID and the same values succeeds and writes nothing, with other values it
/// fails with <see cref="BudgetErrorCodes.BucketIdReused"/> (invariant 13, plan 1a D4).
/// </summary>
/// <param name="Id">The new bucket's ID, a version 7 UUID from the client.</param>
/// <param name="Name">The name (<see cref="BucketNameRules"/>).</param>
/// <param name="Currency">The bucket's currency.</param>
/// <param name="Kind">Savings or investment.</param>
/// <param name="Profile">Required for an investment bucket, <c>null</c> for a savings bucket.</param>
public sealed record CreateBucket(Guid Id, string Name, Currency Currency, BucketKind Kind, InvestmentProfile? Profile)
    : ICommand<Result>, ILedgerCommand;

/// <summary>Shape checks. Whether the name is free is a domain rule (<see cref="Bucket.Create"/>).</summary>
public sealed class CreateBucketValidator : AbstractValidator<CreateBucket>
{
    /// <summary>Creates the rules.</summary>
    public CreateBucketValidator()
    {
        RuleFor(command => command.Name).BucketName();
        RuleFor(command => command.Currency).NotNull();
        RuleFor(command => command.Kind).IsInEnum();
        RuleFor(command => command.Profile).NotNull().When(command => command.Kind == BucketKind.Investment);
        RuleFor(command => command.Profile).Null().When(command => command.Kind == BucketKind.Savings);
        RuleFor(command => command.Profile).IsInEnum().When(command => command.Profile is not null);
    }
}

/// <summary>Reads the state (version first), recognises a repeat, creates the bucket and commits it under the version it read.</summary>
public sealed class CreateBucketHandler(BudgetStateLoader loader, ILedger ledger, IOwnerContext owner) : ICommandHandler<CreateBucket, Result>
{
    /// <inheritdoc />
    public async ValueTask<Result> Handle(CreateBucket command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var id = StronglyTypedId.FromClient<BucketId>(command.Id);
        if (id.IsFailed)
        {
            return id.ToResult();
        }

        var kind = BucketContractMapping.ToDomain(command.Kind);
        var profile = BucketContractMapping.ToDomain(command.Profile);
        var loaded = await loader.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailed)
        {
            return loaded.ToResult();
        }

        var state = loaded.Value;

        if (state.FindBucket(command.Id) is { } existing)
        {
            return existing.HasDefinition(command.Name, command.Currency, kind, profile)
                ? Result.Ok()
                : Result.Fail(AppError.Conflict(BudgetErrorCodes.BucketIdReused));
        }

        var bucket = Bucket.Create(id.Value, owner.OwnerId, command.Name, command.Currency, kind, profile, state.Buckets);
        if (bucket.IsFailed)
        {
            return bucket.ToResult();
        }

        var committed = await ledger.CommitAsync(new LedgerCommit(BudgetLedger.Name, state.Version, saves: [bucket.Value]), cancellationToken)
            .ConfigureAwait(false);
        return committed.ToResult();
    }
}
