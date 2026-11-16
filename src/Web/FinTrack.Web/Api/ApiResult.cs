using System.Text.Json;

namespace FinTrack.Web.Api;

/// <summary>
/// An Api answer without a value (a command answers 204): success, or an error code with its
/// parameters.
/// </summary>
public record ApiResult
{
    private static readonly IReadOnlyDictionary<string, JsonElement> NoParams = new Dictionary<string, JsonElement>();

    /// <summary>Creates a result; <paramref name="errorCode"/> is <see langword="null"/> on success.</summary>
    protected ApiResult(string? errorCode, IReadOnlyDictionary<string, JsonElement>? errorParams)
    {
        ErrorCode = errorCode;
        ErrorParams = errorParams ?? NoParams;
    }

    /// <summary>The stable error code; set when the call failed.</summary>
    public string? ErrorCode { get; }

    /// <summary>The error's parameters, for placeholders in the error text.</summary>
    public IReadOnlyDictionary<string, JsonElement> ErrorParams { get; }

    /// <summary>Whether the call succeeded.</summary>
    public bool IsSuccess => ErrorCode is null;

    internal static ApiResult Success { get; } = new(null, null);

    internal static ApiResult Failure(string code, IReadOnlyDictionary<string, JsonElement>? parameters = null) =>
        new(code, parameters);
}

/// <summary>An Api answer with a value: the value, or an error code with its parameters.</summary>
public sealed record ApiResult<T> : ApiResult
    where T : class
{
    private ApiResult(T? value, string? errorCode, IReadOnlyDictionary<string, JsonElement>? errorParams)
        : base(errorCode, errorParams) => Value = value;

    /// <summary>The value; set when <see cref="ApiResult.IsSuccess"/>.</summary>
    public T? Value { get; }

    internal static new ApiResult<T> Success(T value) => new(value, null, null);

    internal static new ApiResult<T> Failure(string code, IReadOnlyDictionary<string, JsonElement>? parameters = null) =>
        new(null, code, parameters);
}
