using FinTrack.Api.Http;
using FinTrack.Budget.Application.Plans;
using FinTrack.Budget.Contracts.V1.Plans;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace FinTrack.Api.Budget;

/// <summary><c>POST /api/v1/plans/{id}/waivers</c> with a <see cref="WaiveShortfallRequest"/>: 204 when recorded (or already recorded with this content).</summary>
public sealed class WaiveShortfallFunction(ISender sender)
{
    /// <summary>The function name.</summary>
    public const string Name = "WaiveShortfall";

    /// <summary>Records the waiver.</summary>
    [Function(Name)]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/plans/{id}/waivers")] HttpRequest request,
        string id,
        CancellationToken cancellationToken)
    {
        var planId = RequestValues.RouteGuid(id, "id");
        if (planId.IsFailed)
        {
            return ProblemDetailsMapper.ToActionResult(planId.ToResult());
        }

        var body = await RequestBody.ReadAsync<WaiveShortfallRequest>(request, cancellationToken).ConfigureAwait(false);
        if (body.IsFailed)
        {
            return ProblemDetailsMapper.ToActionResult(body.ToResult());
        }

        var waiver = body.Value;
        var command = new WaiveShortfall(waiver.Id, planId.Value, waiver.Amount, waiver.OccurredOn, waiver.Note, waiver.Tags ?? []);
        return ProblemDetailsMapper.ToActionResult(await sender.Send(command, cancellationToken).ConfigureAwait(false));
    }
}
