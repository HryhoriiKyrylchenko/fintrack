using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace FinTrack.Budget.Domain.Journal;

/// <summary>
/// Where an entry came from (ARCHITECTURE 4.2): <c>Manual</c>, <c>Import:&lt;connector&gt;</c> or
/// <c>Agent:&lt;name&gt;</c>. Stored as that text. Only <see cref="Manual"/> exists in phase 1a; imports
/// and agents create drafts that a user confirms (CLAUDE.md rule 13).
/// </summary>
/// <remarks>A connector or agent name is lower-case letters, digits, <c>_</c> and <c>-</c>, starting with a letter.</remarks>
public readonly partial record struct EntrySource
{
    private const string ManualText = "Manual";
    private const string ImportPrefix = "Import:";
    private const string AgentPrefix = "Agent:";

    private EntrySource(string value) => Value = value;

    /// <summary>Entered by the user.</summary>
    public static EntrySource Manual { get; } = new(ManualText);

    /// <summary>The stored text, for example <c>Import:binance</c>. Null only for <c>default(EntrySource)</c>.</summary>
    public string Value { get; }

    /// <summary>Confirmed from an import of <paramref name="connector"/>.</summary>
    /// <exception cref="ArgumentException">The name is not a valid connector name (a bug).</exception>
    public static EntrySource Import(string connector) => Named(ImportPrefix, connector);

    /// <summary>Confirmed from a draft of the agent <paramref name="name"/>.</summary>
    /// <exception cref="ArgumentException">The name is not a valid agent name (a bug).</exception>
    public static EntrySource Agent(string name) => Named(AgentPrefix, name);

    /// <summary>Reads the stored text; <c>false</c> for anything that is not exactly one of the forms.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out EntrySource source)
    {
        source = default;
        if (text == ManualText)
        {
            source = Manual;
            return true;
        }

        foreach (var prefix in new[] { ImportPrefix, AgentPrefix })
        {
            if (text is not null && text.StartsWith(prefix, StringComparison.Ordinal) && NameFormat().IsMatch(text[prefix.Length..]))
            {
                source = new EntrySource(text);
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    private static EntrySource Named(string prefix, string name) =>
        name is not null && NameFormat().IsMatch(name)
            ? new EntrySource(prefix + name)
            : throw new ArgumentException("A connector or agent name is lower-case letters, digits, '_' and '-', starting with a letter.", nameof(name));

    [GeneratedRegex("^[a-z][a-z0-9_-]*\\z")]
    private static partial Regex NameFormat();
}
