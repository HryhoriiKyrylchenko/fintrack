using System.Globalization;
using System.Text.Json;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Web.Api;
using FluentResults;

namespace FinTrack.Web.Resources;

/// <summary>
/// An error to show: its stable code and its parameters as invariant text, whether it came from the Api
/// (<see cref="ApiResult"/>) or from a check in the Web app itself (a kernel <see cref="AppError"/>, such as
/// <c>money.too_many_decimals</c> from <c>MoneyInput</c>). <see cref="SharedResource.ErrorText(Microsoft.Extensions.Localization.IStringLocalizer{SharedResource}, ErrorInfo, CultureInfo)"/> turns it into text.
/// </summary>
/// <param name="Code">The error code, for example <c>bucket.insufficient_funds</c>.</param>
/// <param name="Parameters">The parameters by name: strings as they are, numbers in invariant form, booleans as <c>true</c> or <c>false</c>.</param>
public sealed record ErrorInfo(string Code, IReadOnlyDictionary<string, string> Parameters)
{
    private static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

    /// <summary>The error of a failed call.</summary>
    /// <exception cref="ArgumentException">The call succeeded (a bug).</exception>
    public static ErrorInfo From(ApiResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.ErrorCode is { } code
            ? FromApi(code, result.ErrorParams)
            : throw new ArgumentException("A successful call has no error.", nameof(result));
    }

    /// <summary>An error from the Api's problem body; parameters that are not a string, number or boolean are left out.</summary>
    public static ErrorInfo FromApi(string code, IReadOnlyDictionary<string, JsonElement> parameters)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(parameters);

        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in parameters)
        {
            string? text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };

            if (text is not null)
            {
                texts[name] = text;
            }
        }

        return new ErrorInfo(code, texts);
    }

    /// <summary>
    /// An error from a check in the Web app. Anything but an <see cref="AppError"/> is a bug and is shown as
    /// the general <see cref="ApiClient.UnexpectedResponse"/>; its message is never shown.
    /// </summary>
    public static ErrorInfo From(IError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (error is not AppError appError)
        {
            return new ErrorInfo(ApiClient.UnexpectedResponse, None);
        }

        var texts = appError.Parameters.ToDictionary(
            parameter => parameter.Key,
            parameter => parameter.Value switch
            {
                bool flag => flag ? "true" : "false",
                IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
                var other => other.ToString() ?? string.Empty,
            },
            StringComparer.Ordinal);
        return new ErrorInfo(appError.Code, texts);
    }
}
