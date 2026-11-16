using FinTrack.Api.Http;
using FinTrack.Shared.Infrastructure.Health;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace FinTrack.Api.Health;

/// <summary><c>GET /api/v1/health</c>: 200 when the app and MongoDB answer, otherwise a 503 problem.</summary>
public sealed class GetHealthFunction(ISender sender)
{
    /// <summary>The function name.</summary>
    public const string Name = "GetHealth";

    /// <summary>Runs the health check.</summary>
    [Function(Name)]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/health")] HttpRequest request,
        CancellationToken cancellationToken) =>
        ProblemDetailsMapper.ToActionResult(await sender.Send(new GetHealth(), cancellationToken).ConfigureAwait(false));
}
