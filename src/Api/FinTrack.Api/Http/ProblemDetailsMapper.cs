using FinTrack.Shared.Kernel.Results;
using FluentResults;
using Microsoft.AspNetCore.Mvc;

namespace FinTrack.Api.Http;

/// <summary>
/// Turns a <see cref="Result"/> into the HTTP response (ARCHITECTURE 2.7): success as 200 or 204,
/// failure as RFC 7807 <see cref="ProblemDetails"/> with a stable code and parameters. The
/// response never contains a sentence for users; the UI turns codes into text.
/// </summary>
/// <remarks>
/// <para>Status by <see cref="ErrorKind"/>: Validation 400, NotFound 404, Conflict 409, BusinessRule 422, Unavailable 503. The first error decides.</para>
/// <para>
/// Body: <c>type</c> = <c>urn:fintrack:error:&lt;code&gt;</c>, <c>title</c> = the code, <c>status</c>,
/// <c>code</c> and <c>params</c> of the first error, and <c>errors</c>: every error as <c>{ code, params }</c>
/// (several for validation). A failure without an <see cref="AppError"/> is a bug and becomes
/// <see cref="ServerErrorCode"/> (500) without details.
/// </para>
/// </remarks>
public static class ProblemDetailsMapper
{
    /// <summary>The code of an unexpected failure (a bug or an infrastructure fault).</summary>
    public const string ServerErrorCode = "server.error";

    private const int ServerErrorStatus = 500;
    private const string ProblemContentType = "application/problem+json";
    private const string TypePrefix = "urn:fintrack:error:";

    /// <summary>204 on success, otherwise the problem.</summary>
    public static IActionResult ToActionResult(Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsSuccess ? new NoContentResult() : ToObjectResult(Problem(result.Errors));
    }

    /// <summary>200 with the value on success, otherwise the problem.</summary>
    public static IActionResult ToActionResult<TValue>(Result<TValue> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsSuccess ? new OkObjectResult(result.Value) : ToObjectResult(Problem(result.Errors));
    }

    /// <summary>The response for an unexpected failure: 500 with <see cref="ServerErrorCode"/> and nothing else.</summary>
    public static ProblemDetails UnexpectedError() =>
        Create(ServerErrorStatus, ServerErrorCode, new Dictionary<string, object>(), []);

    /// <summary>Wraps a problem in a result with its status and the <c>application/problem+json</c> content type.</summary>
    public static ObjectResult ToObjectResult(ProblemDetails problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var result = new ObjectResult(problem) { StatusCode = problem.Status };
        result.ContentTypes.Add(ProblemContentType);
        return result;
    }

    private static ProblemDetails Problem(IReadOnlyList<IError> errors)
    {
        if (errors.Count == 0 || errors.Any(error => error is not AppError))
        {
            return UnexpectedError();
        }

        var appErrors = errors.Cast<AppError>().ToList();
        var first = appErrors[0];

        return Create(
            StatusFor(first.Kind),
            first.Code,
            first.Parameters,
            [.. appErrors.Select(error => new ProblemError(error.Code, error.Parameters))]);
    }

    private static ProblemDetails Create(int status, string code, IReadOnlyDictionary<string, object> parameters, IReadOnlyList<ProblemError> errors)
    {
        var problem = new ProblemDetails
        {
            Type = TypePrefix + code,
            Title = code,
            Status = status,
        };

        problem.Extensions["code"] = code;
        problem.Extensions["params"] = parameters;
        problem.Extensions["errors"] = errors;
        return problem;
    }

    private static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => 400,
        ErrorKind.NotFound => 404,
        ErrorKind.Conflict => 409,
        ErrorKind.BusinessRule => 422,
        ErrorKind.Unavailable => 503,
        _ => ServerErrorStatus,
    };
}

/// <summary>One error in a problem's <c>errors</c> list.</summary>
/// <param name="Code">The stable code.</param>
/// <param name="Params">Its parameters (camelCase names).</param>
public sealed record ProblemError(string Code, IReadOnlyDictionary<string, object> Params);
