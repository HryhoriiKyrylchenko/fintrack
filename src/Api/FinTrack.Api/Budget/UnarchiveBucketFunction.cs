using FinTrack.Api.Http;
using FinTrack.Budget.Application.Buckets;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace FinTrack.Api.Budget;

/// <summary><c>POST /api/v1/buckets/{id}/unarchive</c> (no body): 204 when active again (or active already).</summary>
public sealed class UnarchiveBucketFunction(ISender sender)
{
    /// <summary>The function name.</summary>
    public const string Name = "UnarchiveBucket";

    /// <summary>Makes the archived bucket active again.</summary>
    [Function(Name)]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/buckets/{id}/unarchive")] HttpRequest request,
        string id,
        CancellationToken cancellationToken)
    {
        var bucketId = RequestValues.RouteGuid(id, "id");
        if (bucketId.IsFailed)
        {
            return ProblemDetailsMapper.ToActionResult(bucketId.ToResult());
        }

        return ProblemDetailsMapper.ToActionResult(await sender.Send(new UnarchiveBucket(bucketId.Value), cancellationToken).ConfigureAwait(false));
    }
}
