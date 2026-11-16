using System.Net.Http.Json;
using System.Text.Json;

namespace FinTrack.Web.Api;

/// <summary>
/// Calls the Api. A failure is always an error code: the Api's <c>ProblemDetails</c> code
/// (ARCHITECTURE 2.7), or a client-side code when there is no usable answer. The UI turns codes
/// into text from the resource files; it never shows server text.
/// </summary>
public sealed class ApiClient(HttpClient http)
{
    /// <summary>The server could not be reached at all (not running, offline, blocked by CORS).</summary>
    public const string ApiUnreachable = "client.api_unreachable";

    /// <summary>The server answered with something that is not the expected JSON.</summary>
    public const string UnexpectedResponse = "client.unexpected_response";

    private const string ProblemMediaType = "application/problem+json";

    /// <summary>GETs <paramref name="path"/> (relative to the Api base address) and reads the JSON answer.</summary>
    public async Task<ApiResult<T>> GetAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(new Uri(path, UriKind.Relative), cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return ApiResult<T>.Failure(ApiUnreachable);
        }

        using (response)
        {
            try
            {
                if (response.IsSuccessStatusCode)
                {
                    var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false);
                    return value is null ? ApiResult<T>.Failure(UnexpectedResponse) : ApiResult<T>.Success(value);
                }

                if (response.Content.Headers.ContentType?.MediaType == ProblemMediaType
                    && await response.Content.ReadFromJsonAsync<ProblemBody>(cancellationToken).ConfigureAwait(false) is { Code: { Length: > 0 } code } problem)
                {
                    return ApiResult<T>.Failure(code, problem.Params);
                }

                return ApiResult<T>.Failure(UnexpectedResponse);
            }
            catch (JsonException)
            {
                return ApiResult<T>.Failure(UnexpectedResponse);
            }
        }
    }

    /// <summary>The parts of the Api's <c>ProblemDetails</c> the UI uses.</summary>
    private sealed record ProblemBody(string? Code, Dictionary<string, JsonElement>? Params);
}

/// <summary>An Api answer: a value, or an error code with its parameters.</summary>
public sealed record ApiResult<T>
    where T : class
{
    private ApiResult(T? value, string? errorCode, IReadOnlyDictionary<string, JsonElement> errorParams)
    {
        Value = value;
        ErrorCode = errorCode;
        ErrorParams = errorParams;
    }

    /// <summary>The value; set when <see cref="IsSuccess"/>.</summary>
    public T? Value { get; }

    /// <summary>The stable error code; set when the call failed.</summary>
    public string? ErrorCode { get; }

    /// <summary>The error's parameters, for placeholders in the error text.</summary>
    public IReadOnlyDictionary<string, JsonElement> ErrorParams { get; }

    /// <summary>Whether the call returned a value.</summary>
    public bool IsSuccess => ErrorCode is null;

    internal static ApiResult<T> Success(T value) => new(value, null, new Dictionary<string, JsonElement>());

    internal static ApiResult<T> Failure(string code, IReadOnlyDictionary<string, JsonElement>? parameters = null) =>
        new(null, code, parameters ?? new Dictionary<string, JsonElement>());
}
