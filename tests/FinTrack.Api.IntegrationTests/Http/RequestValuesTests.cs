using FinTrack.Api.Http;
using FinTrack.Shared.Kernel.Results;
using FluentResults;
using Microsoft.AspNetCore.Http;

namespace FinTrack.Api.IntegrationTests.Http;

/// <summary>
/// The optional query values of <c>GET /api/v1/journal</c>: absent is <c>null</c>; present, exactly one value in
/// its one canonical spelling, otherwise <c>validation.failed</c> with the parameter and rule <c>QueryFormat</c>.
/// </summary>
public class RequestValuesTests
{
    [Fact]
    public void AbsentValues_AreNull()
    {
        var request = Request(string.Empty);

        RequestValues.QueryGuid(request, "bucketId").Value.ShouldBeNull();
        RequestValues.QueryDate(request, "from").Value.ShouldBeNull();
        RequestValues.QueryInt(request, "limit").Value.ShouldBeNull();
        RequestValues.QueryText(request, "cursor").Value.ShouldBeNull();
    }

    [Fact]
    public void WellFormedValues_AreRead()
    {
        var request = Request("?bucketId=0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b&from=2026-01-05&limit=25&cursor=AQGQobLD1H5fimt8jZ4PGisAC6V2AAABm8EE3Xs");

        RequestValues.QueryGuid(request, "bucketId").Value.ShouldBe(Guid.Parse("0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b"));
        RequestValues.QueryDate(request, "from").Value.ShouldBe(new DateOnly(2026, 1, 5));
        RequestValues.QueryInt(request, "limit").Value.ShouldBe(25);
        RequestValues.QueryText(request, "cursor").Value.ShouldBe("AQGQobLD1H5fimt8jZ4PGisAC6V2AAABm8EE3Xs");
    }

    [Theory]
    [InlineData("?bucketId=not-a-guid")]
    [InlineData("?bucketId=0190a1b2c3d47e5f8a6b7c8d9e0f1a2b")]
    [InlineData("?bucketId={0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b}")]
    [InlineData("?bucketId=")]
    [InlineData("?bucketId=0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b&bucketId=0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b")]
    public void MalformedGuid_IsQueryFormat(string query) => ShouldBeQueryFormat(RequestValues.QueryGuid(Request(query), "bucketId"), "bucketId");

    [Theory]
    [InlineData("?from=2026-1-5")]
    [InlineData("?from=05.01.2026")]
    [InlineData("?from=2026-01-05T00:00:00Z")]
    [InlineData("?from=2026-02-30")]
    [InlineData("?from=")]
    [InlineData("?from=2026-01-05&from=2026-01-06")]
    public void MalformedDate_IsQueryFormat(string query) => ShouldBeQueryFormat(RequestValues.QueryDate(Request(query), "from"), "from");

    [Theory]
    [InlineData("?limit=ten")]
    [InlineData("?limit=+5")]
    [InlineData("?limit=05")]
    [InlineData("?limit=5.0")]
    [InlineData("?limit=%205")]
    [InlineData("?limit=99999999999")]
    [InlineData("?limit=")]
    [InlineData("?limit=5&limit=6")]
    public void MalformedInt_IsQueryFormat(string query) => ShouldBeQueryFormat(RequestValues.QueryInt(Request(query), "limit"), "limit");

    [Theory]
    [InlineData("?limit=0", 0)]
    [InlineData("?limit=-1", -1)]
    public void IntOutOfItsRange_IsStillRead_TheValidatorDecides(string query, int expected) =>
        RequestValues.QueryInt(Request(query), "limit").Value.ShouldBe(expected);

    [Fact]
    public void RepeatedText_IsQueryFormat() => ShouldBeQueryFormat(RequestValues.QueryText(Request("?cursor=a&cursor=b"), "cursor"), "cursor");

    private static void ShouldBeQueryFormat(IResultBase result, string field)
    {
        var error = result.Errors.ShouldHaveSingleItem().ShouldBeOfType<AppError>();
        error.Code.ShouldBe(ErrorCodes.ValidationFailed);
        error.Kind.ShouldBe(ErrorKind.Validation);
        error.Parameters["field"].ShouldBe(field);
        error.Parameters["rule"].ShouldBe(RequestValues.QueryFormatRule);
    }

    private static HttpRequest Request(string query)
    {
        var request = new DefaultHttpContext().Request;
        request.QueryString = new QueryString(query.Length == 0 ? null : query);
        return request;
    }
}
