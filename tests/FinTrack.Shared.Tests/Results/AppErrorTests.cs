using FinTrack.Shared.Kernel.Results;
using FluentResults;

namespace FinTrack.Shared.Tests.Results;

public class AppErrorTests
{
    [Theory]
    [InlineData(ErrorKind.Validation)]
    [InlineData(ErrorKind.NotFound)]
    [InlineData(ErrorKind.Conflict)]
    [InlineData(ErrorKind.BusinessRule)]
    public void Factory_SetsKindAndCode(ErrorKind kind)
    {
        var error = kind switch
        {
            ErrorKind.Validation => AppError.Validation("bucket.insufficient_funds"),
            ErrorKind.NotFound => AppError.NotFound("bucket.insufficient_funds"),
            ErrorKind.Conflict => AppError.Conflict("bucket.insufficient_funds"),
            ErrorKind.BusinessRule => AppError.BusinessRule("bucket.insufficient_funds"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        error.Kind.ShouldBe(kind);
        error.Code.ShouldBe("bucket.insufficient_funds");
        error.Parameters.ShouldBeEmpty();
    }

    [Fact]
    public void Message_IsTheCode_NeverASentence() =>
        AppError.BusinessRule("bucket.insufficient_funds").Message.ShouldBe("bucket.insufficient_funds");

    [Fact]
    public void Parameters_AreKeptByName()
    {
        var error = AppError.Validation(
            "money.too_many_decimals",
            ("currency", "PLN"),
            ("maxDecimalPlaces", 2),
            ("isCrypto", false),
            ("limit", 1500.50m),
            ("count", 3L));

        error.Parameters.ShouldBe(new Dictionary<string, object>
        {
            ["currency"] = "PLN",
            ["maxDecimalPlaces"] = 2,
            ["isCrypto"] = false,
            ["limit"] = 1500.50m,
            ["count"] = 3L,
        });
    }

    [Fact]
    public void AppError_TravelsInsideAResult()
    {
        Result<int> result = Result.Fail(AppError.NotFound("bucket.not_found", ("bucketId", "0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b")));

        var error = result.Errors.ShouldHaveSingleItem().ShouldBeOfType<AppError>();
        error.Kind.ShouldBe(ErrorKind.NotFound);
        error.Parameters["bucketId"].ShouldBe("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");
    }

    [Theory]
    [InlineData("id.invalid")]
    [InlineData("bucket.insufficient_funds")]
    [InlineData("budget.plan.paused_twice")]
    [InlineData("a1.b2")]
    public void Code_InStableFormat_IsAccepted(string code) => AppError.Validation(code).Code.ShouldBe(code);

    [Theory]
    [InlineData("")]
    [InlineData("bucket")] // needs at least two segments
    [InlineData("Bucket.not_found")] // lower case only
    [InlineData("bucket.notFound")]
    [InlineData("bucket.")]
    [InlineData(".not_found")]
    [InlineData("bucket..not_found")]
    [InlineData("bucket.not found")]
    [InlineData("bucket.not-found")]
    [InlineData("1bucket.not_found")]
    [InlineData("bucket.not_found\n")] // '$' would accept a trailing newline; the pattern must not
    public void Code_InAnyOtherFormat_Throws(string code) =>
        Should.Throw<ArgumentException>(() => AppError.Validation(code));

    [Theory]
    [InlineData("Currency")]
    [InlineData("max_places")]
    [InlineData("max-places")]
    [InlineData("")]
    [InlineData("1st")]
    public void ParameterName_NotCamelCase_Throws(string name) =>
        Should.Throw<ArgumentException>(() => AppError.Validation("id.invalid", (name, "x")));

    [Fact]
    public void ParameterName_Duplicate_Throws() =>
        Should.Throw<ArgumentException>(() => AppError.Validation("id.invalid", ("currency", "PLN"), ("currency", "EUR")));

    [Fact]
    public void ParameterValue_Null_Throws() =>
        Should.Throw<ArgumentException>(() => AppError.Validation("id.invalid", ("currency", null!)));

    [Theory]
    [InlineData(1.5d)]
    [InlineData(1.5f)]
    [InlineData('x')]
    public void ParameterValue_OfUnsupportedPrimitive_Throws(object value) =>
        Should.Throw<ArgumentException>(() => AppError.Validation("id.invalid", ("value", value)));

    [Fact]
    public void ParameterValue_OfUnsupportedType_Throws()
    {
        Should.Throw<ArgumentException>(() => AppError.Validation("id.invalid", ("value", new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc))));
        Should.Throw<ArgumentException>(() => AppError.Validation("id.invalid", ("value", Guid.Empty)));
        Should.Throw<ArgumentException>(() => AppError.Validation("id.invalid", ("value", new object())));
    }
}
