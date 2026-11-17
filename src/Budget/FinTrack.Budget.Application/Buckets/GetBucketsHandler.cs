using FinTrack.Budget.Contracts.V1.Buckets;
using FluentResults;
using Mediator;
using DomainStatus = FinTrack.Budget.Domain.Buckets.BucketStatus;

namespace FinTrack.Budget.Application.Buckets;

/// <summary>
/// Answers <see cref="GetBuckets"/>: the owner's buckets as created (by creation time, then ID, so
/// buckets created in one commit keep a stable order); archived ones only on request.
/// </summary>
public sealed class GetBucketsHandler(BudgetStateLoader loader) : IQueryHandler<GetBuckets, Result<IReadOnlyList<BucketDto>>>
{
    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<BucketDto>>> Handle(GetBuckets query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var loaded = await loader.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailed)
        {
            return loaded.ToResult<IReadOnlyList<BucketDto>>();
        }

        var state = loaded.Value;

        IReadOnlyList<BucketDto> buckets =
        [
            .. state.Buckets
                .Where(bucket => query.IncludeArchived || bucket.Status == DomainStatus.Active)
                .OrderBy(bucket => bucket.CreatedAt)
                .ThenBy(bucket => bucket.Id)
                .Select(BucketContractMapping.ToDto),
        ];

        return Result.Ok(buckets);
    }
}
