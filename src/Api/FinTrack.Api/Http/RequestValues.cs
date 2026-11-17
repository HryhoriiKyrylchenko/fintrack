using FinTrack.Shared.Kernel.Results;
using FluentResults;
using Microsoft.AspNetCore.Http;

namespace FinTrack.Api.Http;

/// <summary>
/// Reads values from the route and the query string strictly, as <see cref="RequestBody"/> reads the
/// body: a value that does not fit is one <see cref="ErrorCodes.ValidationFailed"/> error (400) with
/// <c>field</c> = the parameter's name and <c>rule</c> = <see cref="RouteFormatRule"/> or
/// <see cref="QueryFormatRule"/>. The value that was sent is never part of the error.
/// </summary>
/// <remarks>
/// Route constraints such as <c>{id:guid}</c> are not used: a value that does not match would make
/// the host answer a bare 404 without a problem body, which the UI cannot explain.
/// </remarks>
public static class RequestValues
{
    /// <summary>The <c>rule</c> of a route value that does not fit.</summary>
    public const string RouteFormatRule = "RouteFormat";

    /// <summary>The <c>rule</c> of a query value that does not fit.</summary>
    public const string QueryFormatRule = "QueryFormat";

    private const string GuidFormat = "D";

    /// <summary>A GUID route value in the standard form (<c>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</c>).</summary>
    /// <param name="value">The route value.</param>
    /// <param name="field">The route parameter's name, for the error.</param>
    public static Result<Guid> RouteGuid(string? value, string field) =>
        Guid.TryParseExact(value, GuidFormat, out var parsed)
            ? Result.Ok(parsed)
            : Result.Fail<Guid>(AppError.Validation(ErrorCodes.ValidationFailed, ("field", field), ("rule", RouteFormatRule)));

    /// <summary>
    /// An optional yes-or-no query parameter: absent is <c>false</c>; otherwise exactly one value,
    /// <c>true</c> or <c>false</c> in lower case.
    /// </summary>
    public static Result<bool> QueryFlag(HttpRequest request, string name)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Query.TryGetValue(name, out var values))
        {
            return Result.Ok(false);
        }

        return values.Count == 1 && values[0] is "true" or "false"
            ? Result.Ok(values[0] == "true")
            : Result.Fail<bool>(AppError.Validation(ErrorCodes.ValidationFailed, ("field", name), ("rule", QueryFormatRule)));
    }
}
