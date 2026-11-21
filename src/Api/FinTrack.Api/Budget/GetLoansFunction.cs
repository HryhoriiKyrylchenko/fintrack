using FinTrack.Api.Http;
using FinTrack.Budget.Contracts.V1.Loans;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace FinTrack.Api.Budget;

/// <summary><c>GET /api/v1/loans[?includeClosed=true]</c>: 200 with the owner's loans in the order they were borrowed (<see cref="GetLoans"/>).</summary>
public sealed class GetLoansFunction(ISender sender)
{
    /// <summary>The function name.</summary>
    public const string Name = "GetLoans";

    /// <summary>Lists the loans.</summary>
    [Function(Name)]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/loans")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        var includeClosed = RequestValues.QueryFlag(request, "includeClosed");
        if (includeClosed.IsFailed)
        {
            return ProblemDetailsMapper.ToActionResult(includeClosed.ToResult());
        }

        return ProblemDetailsMapper.ToActionResult(await sender.Send(new GetLoans(includeClosed.Value), cancellationToken).ConfigureAwait(false));
    }
}
