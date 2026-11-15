using System.Text.RegularExpressions;

namespace FinTrack.Shared.Tests.Architecture;

/// <summary>
/// Every failure in production code carries an <c>AppError</c> with a stable code
/// (ARCHITECTURE 2.7), never a plain FluentResults error or a free-text message.
/// </summary>
public partial class ErrorConventionTests
{
    [Fact]
    public void SourceCode_CreatesNoPlainErrors()
    {
        var violations = Directory.EnumerateFiles(Path.Combine(Repository.Root.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment => segment is "bin" or "obj"))
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, number: index + 1)))
            .Where(x => PlainError().IsMatch(x.line))
            .Select(x => $"{Path.GetRelativePath(Repository.Root.FullName, x.path)}:{x.number}: {x.line.Trim()}")
            .ToList();

        violations.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("return Result.Fail(new Error(\"id.invalid\"));", true)]
    [InlineData("return Result.Fail<Money>(\"money.too_many_decimals\");", true)]
    [InlineData("return Result.Fail(\"something went wrong\");", true)]
    [InlineData("return Result.Fail<TId>(AppError.Validation(ErrorCodes.IdInvalid));", false)]
    [InlineData("return Result.Fail(error);", false)]
    public void Detector_RecognisesPlainErrors(string line, bool expected) =>
        PlainError().IsMatch(line).ShouldBe(expected);

    // new Error(...), or Fail("...") / Fail<T>("...") with a string literal.
    [GeneratedRegex("""new\s+Error\s*\(|\bFail(<[^>]+>)?\s*\(\s*["$@]""")]
    private static partial Regex PlainError();
}
