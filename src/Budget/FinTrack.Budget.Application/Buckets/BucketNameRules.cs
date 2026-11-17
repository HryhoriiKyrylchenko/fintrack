using FinTrack.Budget.Domain.Buckets;
using FluentValidation;

namespace FinTrack.Budget.Application.Buckets;

/// <summary>
/// The shape of a bucket name (plan 1a D15), as validation rules: each failure becomes
/// <c>validation.failed</c> with <c>field</c> = <c>name</c> and one of these <c>rule</c>s:
/// <c>NotEmptyValidator</c>, <c>MaximumLengthValidator</c>, <see cref="NoSurroundingWhiteSpace"/>,
/// <see cref="NoControlCharacters"/>. A name is never trimmed or corrected: the client sends it clean.
/// </summary>
public static class BucketNameRules
{
    /// <summary>The rule code for a name starting or ending with white space.</summary>
    public const string NoSurroundingWhiteSpace = "NoSurroundingWhiteSpace";

    /// <summary>The rule code for a name with a control character (a tab, a line break).</summary>
    public const string NoControlCharacters = "NoControlCharacters";

    /// <summary>Adds the name rules to <paramref name="rule"/>.</summary>
    public static IRuleBuilderOptions<T, string> BucketName<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty()
            .MaximumLength(Bucket.MaxNameLength)
            .Must(name => name is null || name.Length == 0 || (!char.IsWhiteSpace(name[0]) && !char.IsWhiteSpace(name[^1])))
            .WithErrorCode(NoSurroundingWhiteSpace)
            .Must(name => name is null || !name.Any(char.IsControl))
            .WithErrorCode(NoControlCharacters);
}
