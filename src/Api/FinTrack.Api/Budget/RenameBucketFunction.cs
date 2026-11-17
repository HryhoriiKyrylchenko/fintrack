using FinTrack.Api.Http;
using FinTrack.Budget.Application.Buckets;
using FinTrack.Budget.Contracts.V1.Buckets;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace FinTrack.Api.Budget;

/// <summary><c>POST /api/v1/buckets/{id}/rename</c> with a <see cref="RenameBucketRequest"/>: 204 when renamed.</summary>
public sealed class RenameBucketFunction(ISender sender)
{
    /// <summary>The function name.</summary>
    public const string Name = "RenameBucket";

    /// <summary>Renames the bucket.</summary>
    [Function(Name)]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/buckets/{id}/rename")] HttpRequest request,
        string id,
        CancellationToken cancellationToken)
    {
        var bucketId = RequestValues.RouteGuid(id, "id");
        if (bucketId.IsFailed)
        {
            return ProblemDetailsMapper.ToActionResult(bucketId.ToResult());
        }

        var body = await RequestBody.ReadAsync<RenameBucketRequest>(request, cancellationToken).ConfigureAwait(false);
        if (body.IsFailed)
        {
            return ProblemDetailsMapper.ToActionResult(body.ToResult());
        }

        return ProblemDetailsMapper.ToActionResult(await sender.Send(new RenameBucket(bucketId.Value, body.Value.Name), cancellationToken).ConfigureAwait(false));
    }
}
