using FinTrack.Api.Http;
using FinTrack.Budget.Contracts.V1.Overview;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace FinTrack.Api.Budget;

/// <summary>
/// <c>GET /api/v1/overview</c>: 200 with totals per currency, the total in the reporting currency (or why it is
/// missing) and one summary per active bucket; 404 <c>settings.not_found</c> before the first run.
/// </summary>
public sealed class GetBudgetOverviewFunction(ISender sender)
{
    /// <summary>The function name.</summary>
    public const string Name = "GetBudgetOverview";

    /// <summary>Returns the overview.</summary>
    [Function(Name)]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/overview")] HttpRequest request,
        CancellationToken cancellationToken) =>
        ProblemDetailsMapper.ToActionResult(await sender.Send(new GetBudgetOverview(), cancellationToken).ConfigureAwait(false));
}
