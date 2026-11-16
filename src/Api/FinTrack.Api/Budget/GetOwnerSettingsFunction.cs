using FinTrack.Api.Http;
using FinTrack.Budget.Contracts.V1.Settings;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace FinTrack.Api.Budget;

/// <summary><c>GET /api/v1/settings</c>: 200 with the owner's settings, or 404 <c>settings.not_found</c> before the first save.</summary>
public sealed class GetOwnerSettingsFunction(ISender sender)
{
    /// <summary>The function name.</summary>
    public const string Name = "GetOwnerSettings";

    /// <summary>Reads the settings.</summary>
    [Function(Name)]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/settings")] HttpRequest request,
        CancellationToken cancellationToken) =>
        ProblemDetailsMapper.ToActionResult(await sender.Send(new GetOwnerSettings(), cancellationToken).ConfigureAwait(false));
}
