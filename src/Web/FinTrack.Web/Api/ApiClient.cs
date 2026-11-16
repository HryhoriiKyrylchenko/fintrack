using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FinTrack.Shared.Kernel.Json;

namespace FinTrack.Web.Api;

/// <summary>
/// Calls the Api. A failure is always an error code: the Api's <c>ProblemDetails</c> code
/// (ARCHITECTURE 2.7), or a client-side code when there is no usable answer. The UI turns codes
/// into text from the resource files; it never shows server text.
/// </summary>
/// <remarks>
/// Bodies are written and read with the kernel's JSON contract (<see cref="FinTrackJson"/>), the
/// same options the Api uses. A body that cannot be written (for example an instant with an
/// offset) is a bug in the caller and throws before anything is sent.
/// </remarks>
public sealed class ApiClient(HttpClient http)
{
    /// <summary>The server could not be reached at all (not running, offline, blocked by CORS).</summary>
    public const string ApiUnreachable = "client.api_unreachable";

    /// <summary>The server answered with something that is not the expected JSON.</summary>
    public const string UnexpectedResponse = "client.unexpected_response";

    private const string JsonMediaType = "application/json";
    private const string ProblemMediaType = "application/problem+json";

    /// <summary>GETs <paramref name="path"/> (relative to the Api base address) and reads the JSON answer.</summary>
    public async Task<ApiResult<T>> GetAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Relative(path));
        return await SendAsync(request, ReadValueAsync<T>, ApiResult<T>.Failure, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// POSTs <paramref name="body"/> as JSON to <paramref name="path"/>. Any success status is
    /// success; a command answers 204 and its answer has no value.
    /// </summary>
    public Task<ApiResult> PostAsync<TBody>(string path, TBody body, CancellationToken cancellationToken) =>
        SendBodyAsync(HttpMethod.Post, path, body, cancellationToken);

    /// <summary>PUTs <paramref name="body"/> as JSON to <paramref name="path"/>; otherwise like <see cref="PostAsync"/>.</summary>
    public Task<ApiResult> PutAsync<TBody>(string path, TBody body, CancellationToken cancellationToken) =>
        SendBodyAsync(HttpMethod.Put, path, body, cancellationToken);

    private async Task<ApiResult> SendBodyAsync<TBody>(HttpMethod method, string path, TBody body, CancellationToken cancellationToken)
    {
        // Serialized here, not while sending, so a body the contract cannot write fails as a bug
        // instead of looking like an unreachable server.
        var json = JsonSerializer.Serialize(body, FinTrackJson.Options);

        using var request = new HttpRequestMessage(method, Relative(path))
        {
            Content = new StringContent(json, Encoding.UTF8, JsonMediaType),
        };

        return await SendAsync(request, static (_, _) => Task.FromResult(ApiResult.Success), ApiResult.Failure, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResult> SendAsync<TResult>(
        HttpRequestMessage request,
        Func<HttpContent, CancellationToken, Task<TResult>> readSuccess,
        Func<string, IReadOnlyDictionary<string, JsonElement>?, TResult> failure,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return failure(ApiUnreachable, null);
        }

        using (response)
        {
            try
            {
                if (response.IsSuccessStatusCode)
                {
                    return await readSuccess(response.Content, cancellationToken).ConfigureAwait(false);
                }

                if (response.Content.Headers.ContentType?.MediaType == ProblemMediaType
                    && await response.Content.ReadFromJsonAsync<ProblemBody>(FinTrackJson.Options, cancellationToken).ConfigureAwait(false) is { Code: { Length: > 0 } code } problem)
                {
                    return failure(code, problem.Params);
                }

                return failure(UnexpectedResponse, null);
            }
            catch (JsonException)
            {
                return failure(UnexpectedResponse, null);
            }
        }
    }

    private static async Task<ApiResult<T>> ReadValueAsync<T>(HttpContent content, CancellationToken cancellationToken)
        where T : class
    {
        var value = await content.ReadFromJsonAsync<T>(FinTrackJson.Options, cancellationToken).ConfigureAwait(false);
        return value is null ? ApiResult<T>.Failure(UnexpectedResponse) : ApiResult<T>.Success(value);
    }

    private static Uri Relative(string path) => new(path, UriKind.Relative);

    /// <summary>The parts of the Api's <c>ProblemDetails</c> the UI uses; the rest of the problem is skipped.</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
    private sealed record ProblemBody(string? Code = null, Dictionary<string, JsonElement>? Params = null);
}
