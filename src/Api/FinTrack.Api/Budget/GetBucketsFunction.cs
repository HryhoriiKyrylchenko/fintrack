using FinTrack.Api.Http;
using FinTrack.Budget.Contracts.V1.Buckets;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace FinTrack.Api.Budget;

/// <summary><c>GET /api/v1/buckets[?includeArchived=true]</c>: 200 with the owner's buckets as created.</summary>
public sealed class GetBucketsFunction(ISender sender)
{
    /// <summary>The function name.</summary>
    public const string Name = "GetBuckets";

    /// <summary>Lists the buckets.</summary>
    [Function(Name)]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/buckets")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        var includeArchived = RequestValues.QueryFlag(request, "includeArchived");
        if (includeArchived.IsFailed)
        {
            return ProblemDetailsMapper.ToActionResult(includeArchived.ToResult());
        }

        return ProblemDetailsMapper.ToActionResult(await sender.Send(new GetBuckets(includeArchived.Value), cancellationToken).ConfigureAwait(false));
    }
}
