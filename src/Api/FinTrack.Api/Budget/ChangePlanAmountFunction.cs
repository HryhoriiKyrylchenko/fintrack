using FinTrack.Api.Http;
using FinTrack.Budget.Application.Plans;
using FinTrack.Budget.Contracts.V1.Plans;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace FinTrack.Api.Budget;

/// <summary><c>POST /api/v1/plans/{id}/amount</c> with a <see cref="ChangePlanAmountRequest"/>: 204 when recorded (or already recorded with this content).</summary>
public sealed class ChangePlanAmountFunction(ISender sender)
{
    /// <summary>The function name.</summary>
    public const string Name = "ChangePlanAmount";

    /// <summary>Records the plan's new amount.</summary>
    [Function(Name)]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/plans/{id}/amount")] HttpRequest request,
        string id,
        CancellationToken cancellationToken)
    {
        var planId = RequestValues.RouteGuid(id, "id");
        if (planId.IsFailed)
        {
            return ProblemDetailsMapper.ToActionResult(planId.ToResult());
        }

        var body = await RequestBody.ReadAsync<ChangePlanAmountRequest>(request, cancellationToken).ConfigureAwait(false);
        if (body.IsFailed)
        {
            return ProblemDetailsMapper.ToActionResult(body.ToResult());
        }

        var change = body.Value;
        var command = new ChangePlanAmount(change.Id, planId.Value, change.Amount, change.From, change.OccurredOn, change.Note, change.Tags ?? []);
        return ProblemDetailsMapper.ToActionResult(await sender.Send(command, cancellationToken).ConfigureAwait(false));
    }
}
