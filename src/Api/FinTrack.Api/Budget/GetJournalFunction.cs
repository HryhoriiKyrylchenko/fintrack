using FinTrack.Api.Http;
using FinTrack.Budget.Contracts.V1.Journal;
using FluentResults;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace FinTrack.Api.Budget;

/// <summary>
/// <c>GET /api/v1/journal?bucketId=&amp;from=&amp;to=&amp;limit=&amp;cursor=</c>: 200 with one page of the journal, newest
/// first. Every parameter is optional; one that cannot be read is 400 <c>validation.failed</c> (rule
/// <c>QueryFormat</c>), and the query's own rules follow (<see cref="GetJournal"/>).
/// </summary>
public sealed class GetJournalFunction(ISender sender)
{
    /// <summary>The function name.</summary>
    public const string Name = "GetJournal";

    /// <summary>Returns one page of the journal.</summary>
    [Function(Name)]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/journal")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        var bucketId = RequestValues.QueryGuid(request, "bucketId");
        var from = RequestValues.QueryDate(request, "from");
        var to = RequestValues.QueryDate(request, "to");
        var limit = RequestValues.QueryInt(request, "limit");
        var cursor = RequestValues.QueryText(request, "cursor");

        var read = Result.Merge(bucketId.ToResult(), from.ToResult(), to.ToResult(), limit.ToResult(), cursor.ToResult());
        if (read.IsFailed)
        {
            return ProblemDetailsMapper.ToActionResult(read);
        }

        var query = new GetJournal(bucketId.Value, from.Value, to.Value, limit.Value ?? GetJournal.DefaultLimit, cursor.Value);
        return ProblemDetailsMapper.ToActionResult(await sender.Send(query, cancellationToken).ConfigureAwait(false));
    }
}
