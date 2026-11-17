using FinTrack.Api.Http;
using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Contracts.V1.Journal;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace FinTrack.Api.Budget;

/// <summary><c>POST /api/v1/journal/transfers</c> with an <see cref="AddTransferRequest"/>: 204 when recorded (or already recorded with this content).</summary>
public sealed class AddTransferFunction(ISender sender)
{
    /// <summary>The function name.</summary>
    public const string Name = "AddTransfer";

    /// <summary>Records the transfer.</summary>
    [Function(Name)]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/journal/transfers")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        var body = await RequestBody.ReadAsync<AddTransferRequest>(request, cancellationToken).ConfigureAwait(false);
        if (body.IsFailed)
        {
            return ProblemDetailsMapper.ToActionResult(body.ToResult());
        }

        var transfer = body.Value;
        var command = new AddTransfer(
            transfer.Id, transfer.FromAmount, transfer.ToAmount, transfer.Fee, transfer.OccurredOn,
            transfer.FromBucketId, transfer.FromUnallocated, transfer.ToBucketId, transfer.ToUnallocated, transfer.Note, transfer.Tags ?? []);
        return ProblemDetailsMapper.ToActionResult(await sender.Send(command, cancellationToken).ConfigureAwait(false));
    }
}
