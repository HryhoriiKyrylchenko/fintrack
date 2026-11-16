using FinTrack.Api.Http;
using FinTrack.Budget.Application.Settings;
using FinTrack.Budget.Contracts.V1.Settings;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace FinTrack.Api.Budget;

/// <summary>
/// <c>PUT /api/v1/settings</c> with a <see cref="SaveOwnerSettingsRequest"/>: 204 when saved (or
/// already saved with these values), otherwise a 400 problem.
/// </summary>
public sealed class SaveOwnerSettingsFunction(ISender sender)
{
    /// <summary>The function name.</summary>
    public const string Name = "SaveOwnerSettings";

    /// <summary>Saves the settings.</summary>
    [Function(Name)]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "v1/settings")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        var body = await RequestBody.ReadAsync<SaveOwnerSettingsRequest>(request, cancellationToken).ConfigureAwait(false);
        if (body.IsFailed)
        {
            return ProblemDetailsMapper.ToActionResult(body.ToResult());
        }

        var command = new SaveOwnerSettings(body.Value.ReportingCurrency, body.Value.TimeZone, body.Value.Locale);
        return ProblemDetailsMapper.ToActionResult(await sender.Send(command, cancellationToken).ConfigureAwait(false));
    }
}
