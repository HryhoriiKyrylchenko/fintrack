namespace FinTrack.Shared.Kernel.Ledger;

/// <summary>
/// Names one context's ledger, for example <c>budget</c> or <c>inv</c>. It is also the
/// prefix of that context's collections (<c>budget_journal</c>, <c>budget_ledger_heads</c>,
/// <c>budget_outbox</c>), so it is 2 to 20 lower-case letters or digits, starting with a letter.
/// </summary>
public readonly record struct LedgerName
{
    private const int MinLength = 2;
    private const int MaxLength = 20;

    private LedgerName(string value) => Value = value;

    /// <summary>The name, for example <c>budget</c>. Null only for <c>default(LedgerName)</c>.</summary>
    public string Value { get; }

    /// <summary>Creates a ledger name; throws <see cref="ArgumentException"/> for an invalid one (a bug).</summary>
    public static LedgerName From(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException($"A ledger name has {MinLength} to {MaxLength} lower-case letters or digits and starts with a letter.", nameof(value));
        }

        return new LedgerName(value);
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    private static bool IsValid(string? value) =>
        value is { Length: >= MinLength and <= MaxLength }
        && char.IsAsciiLetterLower(value[0])
        && value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character));
}
