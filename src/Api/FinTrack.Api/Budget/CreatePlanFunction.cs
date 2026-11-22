using FinTrack.Api.Http;
using FinTrack.Budget.Application.Plans;
using FinTrack.Budget.Contracts.V1.Plans;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace FinTrack.Api.Budget;

/// <summary><c>POST /api/v1/plans</c> with a <see cref="CreatePlanRequest"/>: 204 when recorded (or already recorded with this content).</summary>
public sealed class CreatePlanFunction(ISender sender)
{
    /// <summary>The function name.</summary>
    public const string Name = "CreatePlan";

    /// <summary>Records the plan.</summary>
    [Function(Name)]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/plans")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        var body = await RequestBody.ReadAsync<CreatePlanRequest>(request, cancellationToken).ConfigureAwait(false);
        if (body.IsFailed)
        {
            return ProblemDetailsMapper.ToActionResult(body.ToResult());
        }

        var plan = body.Value;
        var command = new CreatePlan(plan.Id, plan.BucketId, plan.Amount, plan.ValidFrom, plan.ValidTo, plan.OccurredOn, plan.Note, plan.Tags ?? []);
        return ProblemDetailsMapper.ToActionResult(await sender.Send(command, cancellationToken).ConfigureAwait(false));
    }
}
