using System.Text;

namespace FinTrack.Budget.Domain.Journal;

/// <summary>
/// The descriptive fields every entry carries (ARCHITECTURE 4.2): a note, tags, the source and the group. A
/// parameter object for the entry factories; the entry stores the fields themselves.
/// </summary>
/// <remarks>
/// <para>Rules (input is validated before it reaches the domain, so a break here is a bug and throws):</para>
/// <list type="bullet">
/// <item><description>note: optional; 1 to <see cref="MaxNoteLength"/> characters, no surrounding white space, no control characters except line breaks;</description></item>
/// <item><description>tags: at most <see cref="MaxTags"/>, each 1 to <see cref="MaxTagLength"/> characters with no surrounding white space and no control characters, distinct without regard to case (after Unicode normalisation).</description></item>
/// </list>
/// </remarks>
public sealed record EntryInfo
{
    /// <summary>The longest note.</summary>
    public const int MaxNoteLength = 500;

    /// <summary>The most tags on one entry.</summary>
    public const int MaxTags = 10;

    /// <summary>The longest tag.</summary>
    public const int MaxTagLength = 30;

    /// <summary>Creates the fields; throws <see cref="ArgumentException"/> for values that break the rules (bugs).</summary>
    /// <param name="note">The note, or <c>null</c>.</param>
    /// <param name="tags">The tags, possibly none.</param>
    /// <param name="source">Where the entry came from.</param>
    /// <param name="groupId">The group of a command that writes several entries (plan 1b B15), or <c>null</c>; never empty.</param>
    public EntryInfo(string? note, IReadOnlyList<string> tags, EntrySource source, Guid? groupId = null)
    {
        ArgumentNullException.ThrowIfNull(tags);

        if (note is not null && !IsValidNote(note))
        {
            throw new ArgumentException($"A note has 1 to {MaxNoteLength} characters, no surrounding white space and no control characters except line breaks.", nameof(note));
        }

        if (!AreValidTags(tags))
        {
            throw new ArgumentException($"At most {MaxTags} distinct tags of 1 to {MaxTagLength} characters, without surrounding white space or control characters.", nameof(tags));
        }

        if (source.Value is null)
        {
            throw new ArgumentException("The source is missing.", nameof(source));
        }

        if (groupId == Guid.Empty)
        {
            throw new ArgumentException("A group ID is never empty.", nameof(groupId));
        }

        Note = note;
        Tags = [.. tags];
        Source = source;
        GroupId = groupId;
    }

    /// <summary>No note, no tags, entered by the user.</summary>
    public static EntryInfo None { get; } = new(null, [], EntrySource.Manual);

    /// <summary>The note, or <c>null</c>.</summary>
    public string? Note { get; }

    /// <summary>The tags, in the order given.</summary>
    public IReadOnlyList<string> Tags { get; }

    /// <summary>Where the entry came from.</summary>
    public EntrySource Source { get; }

    /// <summary>The group of the command that writes this entry with others (plan 1b B15), or <c>null</c> for a single entry.</summary>
    public Guid? GroupId { get; }

    /// <summary>Whether <paramref name="note"/> may be a note.</summary>
    public static bool IsValidNote(string? note) =>
        note is { Length: > 0 and <= MaxNoteLength }
        && !char.IsWhiteSpace(note[0])
        && !char.IsWhiteSpace(note[^1])
        && !note.Any(character => char.IsControl(character) && character is not '\n' and not '\r');

    /// <summary>Whether <paramref name="tag"/> may be a tag.</summary>
    public static bool IsValidTag(string? tag) =>
        tag is { Length: > 0 and <= MaxTagLength }
        && !char.IsWhiteSpace(tag[0])
        && !char.IsWhiteSpace(tag[^1])
        && !tag.Any(char.IsControl);

    /// <summary>Whether <paramref name="tags"/> may be an entry's tags.</summary>
    public static bool AreValidTags(IReadOnlyList<string>? tags) =>
        tags is not null
        && tags.Count <= MaxTags
        && tags.All(IsValidTag)
        && tags.Select(tag => tag.Normalize(NormalizationForm.FormC)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == tags.Count;
}
