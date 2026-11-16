using System.Text.Json;
using System.Text.RegularExpressions;
using FinTrack.Shared.Kernel.Json;
using FinTrack.Shared.Kernel.Results;
using FluentResults;
using Microsoft.AspNetCore.Http;

namespace FinTrack.Api.Http;

/// <summary>
/// Reads a request body with the JSON contract (<see cref="FinTrackJson"/>, ARCHITECTURE 2.7):
/// the "parse input" part of a function.
/// </summary>
/// <remarks>
/// <para>
/// A body that cannot be read (not JSON, empty, <c>null</c>, an amount as a number, a value the
/// contract rejects, an unknown or missing property) is a client error, not a bug: the result
/// fails with one <see cref="ErrorCodes.ValidationFailed"/> error, like a failed validator, so the
/// answer is a 400 problem. Its parameters are <c>rule</c> = <see cref="UnreadableRule"/> and, when
/// the failure is at a property, <c>field</c>: the camelCase path such as <c>price</c>,
/// <c>lines[1]</c> or an unknown property's name, in the format the validation pipeline uses.
/// </para>
/// <para>
/// The value that was sent is never part of the error. A path is used only when every segment is
/// a camelCase name (starting with a letter) or an index, so it can never carry an amount or other
/// free text; any other path is left out. The precise reason (for example a currency the server does not know) is not
/// reported either: the Web app sends only values it built with the kernel's own rules
/// (<c>Money.Create</c>, <c>Currency.FromCode</c>), so this failure means a broken client.
/// </para>
/// </remarks>
public static partial class RequestBody
{
    /// <summary>The <c>rule</c> of a body that does not fit the JSON contract.</summary>
    public const string UnreadableRule = "JsonFormat";

    private const string RootPrefix = "$.";

    /// <summary>Reads the body as <typeparamref name="T"/>.</summary>
    public static async Task<Result<T>> ReadAsync<T>(HttpRequest request, CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var value = await JsonSerializer.DeserializeAsync<T>(request.Body, FinTrackJson.Options, cancellationToken).ConfigureAwait(false);
            return value is null ? Unreadable<T>(path: null) : Result.Ok(value);
        }
        catch (JsonException exception)
        {
            return Unreadable<T>(exception.Path);
        }
    }

    private static Result<T> Unreadable<T>(string? path) =>
        Result.Fail<T>(path is not null && path.StartsWith(RootPrefix, StringComparison.Ordinal) && SafePath().IsMatch(path)
            ? AppError.Validation(ErrorCodes.ValidationFailed, ("field", path[RootPrefix.Length..]), ("rule", UnreadableRule))
            : AppError.Validation(ErrorCodes.ValidationFailed, ("rule", UnreadableRule)));

    // "$.name", "$.name[0].other": camelCase names and indexes only.
    [GeneratedRegex(@"^\$\.[a-z][a-zA-Z0-9]*(\[[0-9]+\]|\.[a-z][a-zA-Z0-9]*)*\z")]
    private static partial Regex SafePath();
}
