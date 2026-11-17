using System.Text;
using FinTrack.Budget.Application.Buckets;
using FinTrack.Budget.Domain.Journal;
using FluentValidation;

namespace FinTrack.Budget.Application.Journal;

/// <summary>
/// The shape of an entry's note and tags (<see cref="EntryInfo"/>), as validation rules. Each failure
/// becomes <c>validation.failed</c> with the field (<c>note</c>, <c>tags</c>, <c>tags[1]</c>) and a rule:
/// <c>NotEmptyValidator</c>, <c>MaximumLengthValidator</c>, <c>NoSurroundingWhiteSpace</c>,
/// <c>NoControlCharacters</c>, <see cref="TooManyTags"/> or <see cref="DistinctTags"/>. Values are never
/// trimmed or corrected.
/// </summary>
public static class EntryRules
{
    /// <summary>The rule code for more than <see cref="EntryInfo.MaxTags"/> tags.</summary>
    public const string TooManyTags = "TooManyTags";

    /// <summary>The rule code for two tags that differ only in case.</summary>
    public const string DistinctTags = "DistinctTags";

    /// <summary>The note rules: optional, but when present 1 to 500 characters; line breaks allowed.</summary>
    public static IRuleBuilderOptions<T, string?> EntryNote<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(note => note is null || note.Length > 0).WithErrorCode("NotEmptyValidator")
            .MaximumLength(EntryInfo.MaxNoteLength)
            .Must(note => string.IsNullOrEmpty(note) || (!char.IsWhiteSpace(note[0]) && !char.IsWhiteSpace(note[^1])))
            .WithErrorCode(BucketNameRules.NoSurroundingWhiteSpace)
            .Must(note => note is null || !note.Any(character => char.IsControl(character) && character is not '\n' and not '\r'))
            .WithErrorCode(BucketNameRules.NoControlCharacters);

    /// <summary>The rules for the tag list (count and distinctness).</summary>
    public static IRuleBuilderOptions<T, IReadOnlyList<string>> EntryTags<T>(this IRuleBuilder<T, IReadOnlyList<string>> rule) =>
        rule.NotNull()
            .Must(tags => tags is null || tags.Count <= EntryInfo.MaxTags).WithErrorCode(TooManyTags)
            .Must(tags => tags is null || tags.Where(tag => tag is not null).Select(tag => tag.Normalize(NormalizationForm.FormC)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == tags.Count)
            .WithErrorCode(DistinctTags);

    /// <summary>The rules for one tag (use with <c>RuleForEach</c>).</summary>
    public static IRuleBuilderOptions<T, string> EntryTag<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty()
            .MaximumLength(EntryInfo.MaxTagLength)
            .Must(tag => string.IsNullOrEmpty(tag) || (!char.IsWhiteSpace(tag[0]) && !char.IsWhiteSpace(tag[^1])))
            .WithErrorCode(BucketNameRules.NoSurroundingWhiteSpace)
            .Must(tag => tag is null || !tag.Any(char.IsControl))
            .WithErrorCode(BucketNameRules.NoControlCharacters);
}
