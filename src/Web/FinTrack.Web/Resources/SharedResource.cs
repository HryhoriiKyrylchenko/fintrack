using System.Globalization;
using System.Text.RegularExpressions;
using FinTrack.Shared.Kernel.Time;
using Microsoft.Extensions.Localization;

namespace FinTrack.Web.Resources;

/// <summary>
/// Marks <c>SharedResource.resx</c>, which holds every user-facing string (DESIGN 7). Components
/// inject <c>IStringLocalizer&lt;SharedResource&gt;</c>; a new language is a new <c>.resx</c> file,
/// not a code change.
/// </summary>
public sealed partial class SharedResource
{
    private const string ErrorPrefix = "Error.";
    private const string UnknownError = "Error.unknown";
    private const string WithParamsSuffix = ".WithParams";

    /// <summary>
    /// The text for an error code (<c>Error.&lt;code&gt;</c> in the resources), or a general text
    /// when the code has none yet.
    /// </summary>
    public static string ErrorText(IStringLocalizer<SharedResource> localizer, string code)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        var text = localizer[ErrorPrefix + code];
        return text.ResourceNotFound ? localizer[UnknownError] : text;
    }

    /// <summary>
    /// The text for <paramref name="error"/> with its parameters: the template <c>Error.&lt;code&gt;.WithParams</c>
    /// when the code has one and every <c>{name}</c> in it has a parameter; otherwise the plain text
    /// (<see cref="ErrorText(IStringLocalizer{SharedResource}, string)"/>). A parameter written as a business date
    /// (<c>yyyy-MM-dd</c>) is shown in <paramref name="culture"/>'s short date, a month (<c>yyyy-MM</c>) in its year-month
    /// pattern; any other value as it is.
    /// </summary>
    public static string ErrorText(IStringLocalizer<SharedResource> localizer, ErrorInfo error, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(culture);

        var template = localizer[ErrorPrefix + error.Code + WithParamsSuffix];
        if (template.ResourceNotFound)
        {
            return ErrorText(localizer, error.Code);
        }

        var complete = true;
        var text = Placeholder().Replace(template.Value, match =>
        {
            if (!error.Parameters.TryGetValue(match.Groups[1].Value, out var value))
            {
                complete = false;
                return match.Value;
            }

            if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return date.ToString("d", culture);
            }

            return YearMonth.TryParse(value, out var month) ? month.FirstDay.ToString("Y", culture) : value;
        });

        return complete ? text : ErrorText(localizer, error.Code);
    }

    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9]*)\}")]
    private static partial Regex Placeholder();
}
