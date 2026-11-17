using FinTrack.Api.Http;
using FinTrack.Budget.Application.Buckets;
using FinTrack.Budget.Contracts.V1.Buckets;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace FinTrack.Api.Budget;

/// <summary><c>POST /api/v1/buckets</c> with a <see cref="CreateBucketRequest"/>: 204 when created (or already created with these values).</summary>
public sealed class CreateBucketFunction(ISender sender)
{
    /// <summary>The function name.</summary>
    public const string Name = "CreateBucket";

    /// <summary>Creates the bucket.</summary>
    [Function(Name)]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/buckets")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        var body = await RequestBody.ReadAsync<CreateBucketRequest>(request, cancellationToken).ConfigureAwait(false);
        if (body.IsFailed)
        {
            return ProblemDetailsMapper.ToActionResult(body.ToResult());
        }

        var command = new CreateBucket(body.Value.Id, body.Value.Name, body.Value.Currency, body.Value.Kind, body.Value.Profile);
        return ProblemDetailsMapper.ToActionResult(await sender.Send(command, cancellationToken).ConfigureAwait(false));
    }
}
