using FinTrack.Budget.Contracts.V1.Buckets;
using FinTrack.Web.Api;

namespace FinTrack.Web.Buckets;

/// <summary>The bucket endpoints of the Api (<c>/api/v1/buckets</c>), through <see cref="ApiClient"/>.</summary>
public sealed class BucketsClient(ApiClient api)
{
    private const string Path = "v1/buckets";

    /// <summary>The owner's buckets, oldest first; archived ones only when <paramref name="includeArchived"/>.</summary>
    public Task<ApiResult<IReadOnlyList<BucketDto>>> ListAsync(bool includeArchived, CancellationToken cancellationToken) =>
        api.GetAsync<IReadOnlyList<BucketDto>>(includeArchived ? Path + "?includeArchived=true" : Path, cancellationToken);

    /// <summary>Creates a bucket; repeating the same request changes nothing.</summary>
    public Task<ApiResult> CreateAsync(CreateBucketRequest request, CancellationToken cancellationToken) =>
        api.PostAsync(Path, request, cancellationToken);

    /// <summary>Renames the bucket <paramref name="id"/>.</summary>
    public Task<ApiResult> RenameAsync(Guid id, RenameBucketRequest request, CancellationToken cancellationToken) =>
        api.PostAsync($"{Path}/{id:D}/rename", request, cancellationToken);

    /// <summary>Archives the bucket <paramref name="id"/>; only an empty bucket can be archived (<c>bucket.not_empty</c>).</summary>
    public Task<ApiResult> ArchiveAsync(Guid id, CancellationToken cancellationToken) =>
        api.PostAsync($"{Path}/{id:D}/archive", cancellationToken);

    /// <summary>Makes the archived bucket <paramref name="id"/> active again; refused while an active bucket has its name (<c>bucket.name_taken</c>).</summary>
    public Task<ApiResult> UnarchiveAsync(Guid id, CancellationToken cancellationToken) =>
        api.PostAsync($"{Path}/{id:D}/unarchive", cancellationToken);
}
