using FinTrack.Api.Http;
using FinTrack.Budget.Application.Plans;
using FinTrack.Budget.Contracts.V1.Plans;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace FinTrack.Api.Budget;

/// <summary><c>POST /api/v1/plans/{id}/pauses/{pauseId}</c> with a <see cref="ChangePlanPauseRequest"/>: 204 when recorded (or already recorded with this content).</summary>
public sealed class ChangePlanPauseFunction(ISender sender)
{
    /// <summary>The function name.</summary>
    public const string Name = "ChangePlanPause";

    /// <summary>Records the pause's new values.</summary>
    [Function(Name)]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/plans/{id}/pauses/{pauseId}")] HttpRequest request,
        string id,
        string pauseId,
        CancellationToken cancellationToken)
    {
        var plan = RequestValues.RouteGuid(id, "id");
        if (plan.IsFailed)
        {
            return ProblemDetailsMapper.ToActionResult(plan.ToResult());
        }

        var pause = RequestValues.RouteGuid(pauseId, "pauseId");
        if (pause.IsFailed)
        {
            return ProblemDetailsMapper.ToActionResult(pause.ToResult());
        }

        var body = await RequestBody.ReadAsync<ChangePlanPauseRequest>(request, cancellationToken).ConfigureAwait(false);
        if (body.IsFailed)
        {
            return ProblemDetailsMapper.ToActionResult(body.ToResult());
        }

        var change = body.Value;
        var command = new ChangePlanPause(change.Id, plan.Value, pause.Value, change.From, change.To, change.Mode, change.OccurredOn, change.Note, change.Tags ?? []);
        return ProblemDetailsMapper.ToActionResult(await sender.Send(command, cancellationToken).ConfigureAwait(false));
    }
}
