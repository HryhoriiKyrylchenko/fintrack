using System.Text.RegularExpressions;

namespace FinTrack.Shared.Kernel;

/// <summary>
/// The format of names that are stored or sent and therefore must never change:
/// error codes, event names and event handler names. Lower-case segments of letters,
/// digits and underscores, separated by dots, at least two segments
/// (for example <c>bucket.insufficient_funds</c>).
/// </summary>
internal static partial class StableName
{
    public const string Example = "bucket.insufficient_funds";

    public static bool IsValid(string? value) => value is not null && Format().IsMatch(value);

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+\\z")]
    private static partial Regex Format();
}
