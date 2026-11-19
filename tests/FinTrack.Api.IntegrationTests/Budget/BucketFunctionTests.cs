using System.Text;
using FinTrack.Api.Budget;
using FinTrack.Api.Http;
using FinTrack.Budget.Contracts.V1.Buckets;
using FinTrack.Shared.Infrastructure.Mongo;
using FinTrack.Shared.Infrastructure.Owners;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace FinTrack.Api.IntegrationTests.Budget;

/// <summary>
/// <c>GET</c> and <c>POST /api/v1/buckets</c>, <c>POST /api/v1/buckets/{id}/rename</c> and
/// <c>POST /api/v1/buckets/{id}/unarchive</c> (with archiving) through the
/// Api's own container: route, query and body reading, the pipeline (validation, retry), the handlers
/// and MongoDB. Each test is a new owner.
/// </summary>
public sealed class BucketFunctionTests(ApiFixture fixture)
{
    private readonly OwnerId owner = OwnerId.From(Guid.NewGuid());

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_Is204_AndTheBucketIsListed()
    {
        var id = Guid.CreateVersion7();

        (await CreateAsync(Body(id, "Cushion"))).ShouldBeOfType<NoContentResult>();

        var buckets = (await ListAsync()).ShouldBeOfType<OkObjectResult>().Value.ShouldBeAssignableTo<IReadOnlyList<BucketDto>>()!;
        buckets.ShouldHaveSingleItem().ShouldBe(new BucketDto(id, "Cushion", Currency.Pln, BucketKind.Savings, null, BucketStatus.Active, BucketSystemRole.None));
    }

    [Fact]
    public async Task Create_Investment_WithItsProfile()
    {
        var id = Guid.CreateVersion7();

        (await CreateAsync($$"""{"id":"{{id}}","name":"Crypto","currency":"USD","kind":"Investment","profile":"Crypto"}""")).ShouldBeOfType<NoContentResult>();

        Buckets(await ListAsync()).ShouldHaveSingleItem().Profile.ShouldBe(InvestmentProfile.Crypto);
    }

    [Fact]
    public async Task RepeatedCreate_Is204_AndOtherValuesAre409IdReused()
    {
        var id = Guid.CreateVersion7();
        await CreateAsync(Body(id, "Cushion"));

        (await CreateAsync(Body(id, "Cushion"))).ShouldBeOfType<NoContentResult>();
        Code(await CreateAsync(Body(id, "Pillow")), 409).ShouldBe("bucket.id_reused");
    }

    [Fact]
    public async Task Create_WithAnIdThatIsNotVersion7_Is400IdInvalid() =>
        Code(await CreateAsync(Body(Guid.NewGuid(), "Cushion")), 400).ShouldBe(ErrorCodes.IdInvalid);

    [Fact]
    public async Task Create_WithATakenName_Is422NameTaken()
    {
        await CreateAsync(Body(Guid.CreateVersion7(), "Cushion"));

        Code(await CreateAsync(Body(Guid.CreateVersion7(), "CUSHION")), 422).ShouldBe("bucket.name_taken");
    }

    [Theory]
    [InlineData("""{"id":"ID","name":" Cushion","currency":"PLN","kind":"Savings"}""", "name", "NoSurroundingWhiteSpace")]
    [InlineData("""{"id":"ID","name":"Crypto","currency":"USD","kind":"Investment"}""", "profile", "NotNullValidator")]
    [InlineData("""{"id":"ID","name":"Cushion","currency":"PLN","kind":"Savings","profile":"Crypto"}""", "profile", "NullValidator")]
    [InlineData("""{"id":"ID","name":"Cushion","currency":"PLN","kind":"Pension"}""", "kind", RequestBody.UnreadableRule)]
    [InlineData("""{"id":"ID","name":"Cushion","currency":"PLN","kind":0}""", "kind", RequestBody.UnreadableRule)]
    public async Task Create_WithInvalidInput_Is400ValidationFailed_AtTheField(string template, string field, string rule)
    {
        var parameters = Parameters(await CreateAsync(template.Replace("ID", Guid.CreateVersion7().ToString(), StringComparison.Ordinal)), 400);

        parameters["field"].ShouldBe(field);
        parameters["rule"].ShouldBe(rule);
    }

    [Fact]
    public async Task Rename_Is204_AndTheNewNameIsListed()
    {
        var id = Guid.CreateVersion7();
        await CreateAsync(Body(id, "Cushion"));

        (await RenameAsync(id.ToString(), """{"name":"Safety net"}""")).ShouldBeOfType<NoContentResult>();

        Buckets(await ListAsync()).ShouldHaveSingleItem().Name.ShouldBe("Safety net");
    }

    [Fact]
    public async Task Rename_OfAnUnknownBucket_Is404() =>
        Code(await RenameAsync(Guid.CreateVersion7().ToString(), """{"name":"Anything"}"""), 404).ShouldBe("bucket.not_found");

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("0190a1b2c3d47e5f8a6b7c8d9e0f1a2b")]
    [InlineData("")]
    public async Task Rename_WithAMalformedId_Is400RouteFormat(string id)
    {
        var parameters = Parameters(await RenameAsync(id, """{"name":"Anything"}"""), 400);

        parameters["field"].ShouldBe("id");
        parameters["rule"].ShouldBe(RequestValues.RouteFormatRule);
    }

    [Theory]
    [InlineData("?includeArchived=yes")]
    [InlineData("?includeArchived=True")]
    [InlineData("?includeArchived=true&includeArchived=false")]
    public async Task List_WithAMalformedFlag_Is400QueryFormat(string query)
    {
        var parameters = Parameters(await ListAsync(query), 400);

        parameters["field"].ShouldBe("includeArchived");
        parameters["rule"].ShouldBe(RequestValues.QueryFormatRule);
    }

    [Fact]
    public async Task List_WithArchivedRequested_IsOk() =>
        (await ListAsync("?includeArchived=true")).ShouldBeOfType<OkObjectResult>();

    [Fact]
    public async Task Unarchive_Is204_AndTheBucketIsActiveAgain_AndRepeatingIt_Is204()
    {
        var id = Guid.CreateVersion7();
        await CreateAsync(Body(id, "Holiday"));
        (await ArchiveAsync(id.ToString())).ShouldBeOfType<NoContentResult>();
        Buckets(await ListAsync()).ShouldBeEmpty();

        (await UnarchiveAsync(id.ToString())).ShouldBeOfType<NoContentResult>();
        (await UnarchiveAsync(id.ToString())).ShouldBeOfType<NoContentResult>();

        Buckets(await ListAsync()).ShouldHaveSingleItem().Status.ShouldBe(BucketStatus.Active);
    }

    [Fact]
    public async Task Unarchive_WhileTheNameIsTaken_Is422NameTaken()
    {
        var id = Guid.CreateVersion7();
        await CreateAsync(Body(id, "Holiday"));
        await ArchiveAsync(id.ToString());
        await CreateAsync(Body(Guid.CreateVersion7(), "HOLIDAY"));

        Code(await UnarchiveAsync(id.ToString()), 422).ShouldBe("bucket.name_taken");
    }

    [Fact]
    public async Task Unarchive_OfAnUnknownBucket_Is404() =>
        Code(await UnarchiveAsync(Guid.CreateVersion7().ToString()), 404).ShouldBe("bucket.not_found");

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public async Task Unarchive_WithAMalformedId_Is400RouteFormat(string id)
    {
        var parameters = Parameters(await UnarchiveAsync(id), 400);

        parameters["field"].ShouldBe("id");
        parameters["rule"].ShouldBe(RequestValues.RouteFormatRule);
    }

    private static string Body(Guid id, string name) =>
        $$"""{"id":"{{id}}","name":"{{name}}","currency":"PLN","kind":"Savings"}""";

    private async Task<IActionResult> CreateAsync(string json)
    {
        await using var scope = await ScopeAsync();
        return await ActivatorUtilities.CreateInstance<CreateBucketFunction>(scope.ServiceProvider).Run(JsonRequest(json), Cancellation);
    }

    private async Task<IActionResult> RenameAsync(string id, string json)
    {
        await using var scope = await ScopeAsync();
        return await ActivatorUtilities.CreateInstance<RenameBucketFunction>(scope.ServiceProvider).Run(JsonRequest(json), id, Cancellation);
    }

    private async Task<IActionResult> ArchiveAsync(string id)
    {
        await using var scope = await ScopeAsync();
        return await ActivatorUtilities.CreateInstance<ArchiveBucketFunction>(scope.ServiceProvider).Run(new DefaultHttpContext().Request, id, Cancellation);
    }

    private async Task<IActionResult> UnarchiveAsync(string id)
    {
        await using var scope = await ScopeAsync();
        return await ActivatorUtilities.CreateInstance<UnarchiveBucketFunction>(scope.ServiceProvider).Run(new DefaultHttpContext().Request, id, Cancellation);
    }

    private async Task<IActionResult> ListAsync(string query = "")
    {
        await using var scope = await ScopeAsync();
        var request = new DefaultHttpContext().Request;
        request.QueryString = new QueryString(query.Length == 0 ? null : query);
        return await ActivatorUtilities.CreateInstance<GetBucketsFunction>(scope.ServiceProvider).Run(request, Cancellation);
    }

    // What the worker middleware does per request: indexes once, then the owner.
    private async Task<AsyncServiceScope> ScopeAsync()
    {
        await fixture.Services.GetRequiredService<MongoIndexRunner>().EnsureCreatedAsync(Cancellation);
        var scope = fixture.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedOwnerContext>().Set(owner);
        return scope;
    }

    private static HttpRequest JsonRequest(string json)
    {
        var request = new DefaultHttpContext().Request;
        request.ContentType = "application/json";
        request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return request;
    }

    private static IReadOnlyList<BucketDto> Buckets(IActionResult result) =>
        result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeAssignableTo<IReadOnlyList<BucketDto>>()!;

    private static string Code(IActionResult result, int status) =>
        (string)Problem(result, status).Extensions["code"]!;

    private static IReadOnlyDictionary<string, object> Parameters(IActionResult result, int status)
    {
        var problem = Problem(result, status);
        problem.Extensions["code"].ShouldBe(ErrorCodes.ValidationFailed);
        return problem.Extensions["params"].ShouldBeAssignableTo<IReadOnlyDictionary<string, object>>()!;
    }

    private static ProblemDetails Problem(IActionResult result, int status)
    {
        var objectResult = result.ShouldBeOfType<ObjectResult>();
        objectResult.StatusCode.ShouldBe(status);
        return objectResult.Value.ShouldBeOfType<ProblemDetails>();
    }
}
