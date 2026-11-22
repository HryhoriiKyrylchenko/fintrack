using FinTrack.Api.Http;
using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Contracts.V1.Journal;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace FinTrack.Api.Budget;

/// <summary><c>POST /api/v1/journal/deposits</c> with an <see cref="AddDepositRequest"/>: 204 when recorded (or already recorded with this content).</summary>
public sealed class AddDepositFunction(ISender sender)
{
    /// <summary>The function name.</summary>
    public const string Name = "AddDeposit";

    /// <summary>Records the deposit.</summary>
    [Function(Name)]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/journal/deposits")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        var body = await RequestBody.ReadAsync<AddDepositRequest>(request, cancellationToken).ConfigureAwait(false);
        if (body.IsFailed)
        {
            return ProblemDetailsMapper.ToActionResult(body.ToResult());
        }

        var deposit = body.Value;
        var command = new AddDeposit(deposit.Id, deposit.Amount, deposit.OccurredOn, deposit.BucketId, deposit.Unallocated, deposit.Note, deposit.Tags ?? [], deposit.PlanId);
        return ProblemDetailsMapper.ToActionResult(await sender.Send(command, cancellationToken).ConfigureAwait(false));
    }
}
