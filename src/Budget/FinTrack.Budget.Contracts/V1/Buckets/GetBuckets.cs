using FluentResults;
using Mediator;

namespace FinTrack.Budget.Contracts.V1.Buckets;

/// <summary>
/// The owner's buckets, as created (oldest first). Archived buckets are left out unless
/// <paramref name="IncludeArchived"/> is set. <c>GET /api/v1/buckets[?includeArchived=true]</c>.
/// </summary>
/// <param name="IncludeArchived">Whether archived buckets are listed too.</param>
public sealed record GetBuckets(bool IncludeArchived = false) : IQuery<Result<IReadOnlyList<BucketDto>>>;
