using FinTrack.Api.Http;
using FinTrack.Budget.Application.Plans;
using FinTrack.Budget.Contracts.V1.Plans;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace FinTrack.Api.Budget;

/// <summary><c>POST /api/v1/plans/{id}/pauses</c> with a <see cref="PausePlanRequest"/>: 204 when recorded (or already recorded with this content).</summary>
public sealed class PausePlanFunction(ISender sender)
{
    /// <summary>The function name.</summary>
    public const string Name = "PausePlan";

    /// <summary>Records the pause.</summary>
    [Function(Name)]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/plans/{id}/pauses")] HttpRequest request,
        string id,
        CancellationToken cancellationToken)
    {
        var planId = RequestValues.RouteGuid(id, "id");
        if (planId.IsFailed)
        {
            return ProblemDetailsMapper.ToActionResult(planId.ToResult());
        }

        var body = await RequestBody.ReadAsync<PausePlanRequest>(request, cancellationToken).ConfigureAwait(false);
        if (body.IsFailed)
        {
            return ProblemDetailsMapper.ToActionResult(body.ToResult());
        }

        var pause = body.Value;
        var command = new PausePlan(pause.Id, planId.Value, pause.From, pause.To, pause.Mode, pause.OccurredOn, pause.Note, pause.Tags ?? []);
        return ProblemDetailsMapper.ToActionResult(await sender.Send(command, cancellationToken).ConfigureAwait(false));
    }
}
