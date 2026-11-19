using System.Globalization;
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
    private const string DateFormat = "yyyy-MM-dd";

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
            : QueryFormatError<bool>(name);
    }

    /// <summary>An optional GUID query parameter in the standard form; absent is <c>null</c>.</summary>
    public static Result<Guid?> QueryGuid(HttpRequest request, string name) =>
        Optional<Guid>(request, name, (string text, out Guid value) => Guid.TryParseExact(text, GuidFormat, out value));

    /// <summary>An optional date query parameter as <c>yyyy-MM-dd</c>; absent is <c>null</c>.</summary>
    public static Result<DateOnly?> QueryDate(HttpRequest request, string name) =>
        Optional<DateOnly>(request, name, (string text, out DateOnly value) =>
            DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out value));

    /// <summary>
    /// An optional whole-number query parameter in its one canonical spelling (no sign but <c>-</c>, no
    /// leading zeros, no white space); absent is <c>null</c>. Its range is for the query's validator to judge.
    /// </summary>
    public static Result<int?> QueryInt(HttpRequest request, string name) =>
        Optional<int>(request, name, (string text, out int value) =>
            int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value)
            && string.Equals(value.ToString(CultureInfo.InvariantCulture), text, StringComparison.Ordinal));

    /// <summary>An optional text query parameter, passed on as sent (its format is for the query's validator); absent is <c>null</c>.</summary>
    public static Result<string?> QueryText(HttpRequest request, string name)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Query.TryGetValue(name, out var values))
        {
            return Result.Ok<string?>(null);
        }

        return values.Count == 1 && values[0] is { } text
            ? Result.Ok<string?>(text)
            : QueryFormatError<string?>(name);
    }

    private delegate bool Parser<TValue>(string text, out TValue value);

    // Absent is null; present, exactly one value that the parser accepts.
    private static Result<TValue?> Optional<TValue>(HttpRequest request, string name, Parser<TValue> parse)
        where TValue : struct
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Query.TryGetValue(name, out var values))
        {
            return Result.Ok<TValue?>(null);
        }

        return values.Count == 1 && values[0] is { } text && parse(text, out var value)
            ? Result.Ok<TValue?>(value)
            : QueryFormatError<TValue?>(name);
    }

    private static Result<TValue> QueryFormatError<TValue>(string name) =>
        Result.Fail<TValue>(AppError.Validation(ErrorCodes.ValidationFailed, ("field", name), ("rule", QueryFormatRule)));
}
