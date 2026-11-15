using FinTrack.Shared.Kernel.Results;
using FluentResults;

namespace FinTrack.Tests.Common;

/// <summary>Assertions for results built with <see cref="AppError"/>.</summary>
internal static class ResultAssertions
{
    /// <summary>
    /// Asserts that <paramref name="result"/> failed with exactly one <see cref="AppError"/>
    /// of the given code and kind, and returns it for further checks (for example parameters).
    /// </summary>
    public static AppError ShouldFailWith(this IResultBase result, string code, ErrorKind kind)
    {
        result.IsFailed.ShouldBeTrue("the result should have failed");

        var error = result.Errors.ShouldHaveSingleItem().ShouldBeOfType<AppError>();
        error.Code.ShouldBe(code);
        error.Kind.ShouldBe(kind);

        return error;
    }
}
