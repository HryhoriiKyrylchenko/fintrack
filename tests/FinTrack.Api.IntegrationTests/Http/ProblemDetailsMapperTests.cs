using FinTrack.Api.Http;
using FinTrack.Shared.Kernel.Results;
using FluentResults;
using Microsoft.AspNetCore.Mvc;

namespace FinTrack.Api.IntegrationTests.Http;

/// <summary>
/// RFC 7807 responses built from results: a stable code and parameters, never a sentence for
/// users (ARCHITECTURE 2.7).
/// </summary>
public class ProblemDetailsMapperTests
{
    [Fact]
    public void Success_WithoutValue_IsNoContent() =>
        ProblemDetailsMapper.ToActionResult(Result.Ok()).ShouldBeOfType<NoContentResult>();

    [Fact]
    public void Success_WithValue_IsOkWithTheValue() =>
        ProblemDetailsMapper.ToActionResult(Result.Ok("value")).ShouldBeOfType<OkObjectResult>().Value.ShouldBe("value");

    [Theory]
    [InlineData(ErrorKind.Validation, 400)]
    [InlineData(ErrorKind.NotFound, 404)]
    [InlineData(ErrorKind.Conflict, 409)]
    [InlineData(ErrorKind.BusinessRule, 422)]
    [InlineData(ErrorKind.Unavailable, 503)]
    public void EachKind_HasItsStatus(ErrorKind kind, int status)
    {
        var problem = Problem(ProblemDetailsMapper.ToActionResult(Result.Fail(Error(kind, "bucket.insufficient_funds"))), status);

        problem.Status.ShouldBe(status);
    }

    [Fact]
    public void Failure_CarriesTheCodeAndParameters_AsTheContract()
    {
        var error = AppError.BusinessRule("bucket.insufficient_funds", ("currency", "PLN"), ("maxDecimalPlaces", 2));

        var problem = Problem(ProblemDetailsMapper.ToActionResult(Result.Fail<string>(error)), 422);

        problem.Type.ShouldBe("urn:fintrack:error:bucket.insufficient_funds");
        problem.Title.ShouldBe("bucket.insufficient_funds");
        problem.Detail.ShouldBeNull();
        problem.Extensions["code"].ShouldBe("bucket.insufficient_funds");
        problem.Extensions["params"].ShouldBeAssignableTo<IReadOnlyDictionary<string, object>>()!
            .ShouldBe(new Dictionary<string, object> { ["currency"] = "PLN", ["maxDecimalPlaces"] = 2 }, ignoreOrder: true);
    }

    [Fact]
    public void SeveralErrors_AreAllListed_AndTheFirstDecidesTheStatus()
    {
        var result = Result.Fail(
        [
            AppError.Validation(ErrorCodes.ValidationFailed, ("field", "note"), ("rule", "NotEmptyValidator")),
            AppError.Validation(ErrorCodes.ValidationFailed, ("field", "amount"), ("rule", "GreaterThanValidator")),
        ]);

        var problem = Problem(ProblemDetailsMapper.ToActionResult(result), 400);

        var errors = problem.Extensions["errors"].ShouldBeAssignableTo<IReadOnlyList<ProblemError>>()!;
        errors.Select(error => error.Params["field"]).ShouldBe(["note", "amount"]);
        errors.ShouldAllBe(error => error.Code == ErrorCodes.ValidationFailed);
    }

    [Fact]
    public void ErrorThatIsNotAnAppError_IsAnUnexpectedServerError()
    {
        var problem = Problem(ProblemDetailsMapper.ToActionResult(Result.Fail("a plain message")), 500);

        problem.Extensions["code"].ShouldBe(ProblemDetailsMapper.ServerErrorCode);
        problem.Title.ShouldBe(ProblemDetailsMapper.ServerErrorCode);
        problem.Extensions.Values.ShouldAllBe(value => value == null || !value.ToString()!.Contains("a plain message", StringComparison.Ordinal));
    }

    [Fact]
    public void UnexpectedError_HasNoDetails()
    {
        var problem = ProblemDetailsMapper.UnexpectedError();

        problem.Status.ShouldBe(500);
        problem.Extensions["code"].ShouldBe(ProblemDetailsMapper.ServerErrorCode);
        problem.Detail.ShouldBeNull();
    }

    private static AppError Error(ErrorKind kind, string code) => kind switch
    {
        ErrorKind.Validation => AppError.Validation(code),
        ErrorKind.NotFound => AppError.NotFound(code),
        ErrorKind.Conflict => AppError.Conflict(code),
        ErrorKind.BusinessRule => AppError.BusinessRule(code),
        ErrorKind.Unavailable => AppError.Unavailable(code),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static ProblemDetails Problem(IActionResult result, int status)
    {
        var objectResult = result.ShouldBeOfType<ObjectResult>();
        objectResult.StatusCode.ShouldBe(status);
        objectResult.ContentTypes.ShouldBe(["application/problem+json"]);
        return objectResult.Value.ShouldBeOfType<ProblemDetails>();
    }
}
