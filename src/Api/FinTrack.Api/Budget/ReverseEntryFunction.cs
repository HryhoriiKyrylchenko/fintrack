using FinTrack.Api.Http;
using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Contracts.V1.Journal;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace FinTrack.Api.Budget;

/// <summary><c>POST /api/v1/journal/reversals</c> with a <see cref="ReverseEntryRequest"/>: 204 when recorded (or already recorded with this content).</summary>
public sealed class ReverseEntryFunction(ISender sender)
{
    /// <summary>The function name.</summary>
    public const string Name = "ReverseEntry";

    /// <summary>Records the reversal.</summary>
    [Function(Name)]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/journal/reversals")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        var body = await RequestBody.ReadAsync<ReverseEntryRequest>(request, cancellationToken).ConfigureAwait(false);
        if (body.IsFailed)
        {
            return ProblemDetailsMapper.ToActionResult(body.ToResult());
        }

        var reversal = body.Value;
        var command = new ReverseEntry(reversal.Id, reversal.ReversedEntryId, reversal.OccurredOn, reversal.Note, reversal.Tags ?? []);
        return ProblemDetailsMapper.ToActionResult(await sender.Send(command, cancellationToken).ConfigureAwait(false));
    }
}
