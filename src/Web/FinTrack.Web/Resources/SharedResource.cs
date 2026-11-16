using Microsoft.Extensions.Localization;

namespace FinTrack.Web.Resources;

/// <summary>
/// Marks <c>SharedResource.resx</c>, which holds every user-facing string (DESIGN 7). Components
/// inject <c>IStringLocalizer&lt;SharedResource&gt;</c>; a new language is a new <c>.resx</c> file,
/// not a code change.
/// </summary>
public sealed class SharedResource
{
    private const string ErrorPrefix = "Error.";
    private const string UnknownError = "Error.unknown";

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
}
