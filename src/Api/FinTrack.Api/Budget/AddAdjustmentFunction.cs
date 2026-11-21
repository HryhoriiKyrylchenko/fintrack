using FinTrack.Api.Http;
using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Contracts.V1.Journal;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace FinTrack.Api.Budget;

/// <summary><c>POST /api/v1/journal/adjustments</c> with an <see cref="AddAdjustmentRequest"/>: 204 when recorded (or already recorded with this content).</summary>
public sealed class AddAdjustmentFunction(ISender sender)
{
    /// <summary>The function name.</summary>
    public const string Name = "AddAdjustment";

    /// <summary>Records the entry.</summary>
    [Function(Name)]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/journal/adjustments")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        var body = await RequestBody.ReadAsync<AddAdjustmentRequest>(request, cancellationToken).ConfigureAwait(false);
        if (body.IsFailed)
        {
            return ProblemDetailsMapper.ToActionResult(body.ToResult());
        }

        var entry = body.Value;
        var command = new AddAdjustment(entry.Id, entry.Amount, entry.Direction, entry.OccurredOn, entry.BucketId, entry.Unallocated, entry.Note, entry.Tags ?? []);
        return ProblemDetailsMapper.ToActionResult(await sender.Send(command, cancellationToken).ConfigureAwait(false));
    }
}
