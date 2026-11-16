using System.Text;
using FinTrack.Api.Budget;
using FinTrack.Api.Http;
using FinTrack.Budget.Contracts.V1.Settings;
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
/// <c>GET</c> and <c>PUT /api/v1/settings</c> through the Api's own container: body reading,
/// the mediator pipeline (validation, retry), the handlers and MongoDB. Each test is a new owner.
/// </summary>
public sealed class SettingsFunctionTests(ApiFixture fixture)
{
    private const string Valid = """{"reportingCurrency":"PLN","timeZone":"Europe/Warsaw","locale":"pl-PL"}""";

    private readonly OwnerId owner = OwnerId.From(Guid.NewGuid());

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Get_BeforeTheFirstSave_Is404SettingsNotFound()
    {
        var problem = Problem(await GetAsync(), 404);

        problem.Extensions["code"].ShouldBe("settings.not_found");
    }

    [Fact]
    public async Task Put_Is204_AndGetReturnsTheSettings()
    {
        (await PutAsync(Valid)).ShouldBeOfType<NoContentResult>();

        var settings = (await GetAsync()).ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<OwnerSettingsDto>();
        settings.ShouldBe(new OwnerSettingsDto(Currency.Pln, "Europe/Warsaw", "pl-PL"));
    }

    [Fact]
    public async Task RepeatedPut_Is204()
    {
        await PutAsync(Valid);

        (await PutAsync(Valid)).ShouldBeOfType<NoContentResult>();
    }

    [Fact]
    public async Task PutThatChangesTheValues_IsSaved()
    {
        await PutAsync(Valid);

        await PutAsync("""{"reportingCurrency":"EUR","timeZone":"Europe/Berlin","locale":"de-DE"}""");

        (await GetAsync()).ShouldBeOfType<OkObjectResult>().Value.ShouldBe(new OwnerSettingsDto(Currency.Eur, "Europe/Berlin", "de-DE"));
    }

    [Theory]
    [InlineData("""{"reportingCurrency":"PLN","timeZone":"Mars/Olympus","locale":"pl-PL"}""", "settings.time_zone_unknown")]
    [InlineData("""{"reportingCurrency":"PLN","timeZone":"Europe/Warsaw","locale":"pl-pl"}""", "settings.locale_unknown")]
    public async Task PutWithAnUnknownValue_Is400WithItsCode(string body, string code)
    {
        Problem(await PutAsync(body), 400).Extensions["code"].ShouldBe(code);

        Problem(await GetAsync(), 404);
    }

    [Theory]
    [InlineData("""{"reportingCurrency":"XYZ","timeZone":"Europe/Warsaw","locale":"pl-PL"}""", "reportingCurrency")]
    [InlineData("""{"reportingCurrency":"PLN","timeZone":"Europe/Warsaw"}""", null)]
    [InlineData("""{"reportingCurrency":"PLN","timeZone":"Europe/Warsaw","locale":"pl-PL","extra":1}""", "extra")]
    public async Task PutWithABodyThatDoesNotFitTheContract_Is400JsonFormat(string body, string? field)
    {
        var problem = Problem(await PutAsync(body), 400);

        problem.Extensions["code"].ShouldBe(ErrorCodes.ValidationFailed);
        var parameters = problem.Extensions["params"].ShouldBeAssignableTo<IReadOnlyDictionary<string, object>>()!;
        parameters["rule"].ShouldBe(RequestBody.UnreadableRule);
        if (field is null)
        {
            parameters.ShouldNotContainKey("field");
        }
        else
        {
            parameters["field"].ShouldBe(field);
        }
    }

    [Fact]
    public async Task PutWithAnEmptyTimeZone_IsRejectedByTheValidator()
    {
        var problem = Problem(await PutAsync("""{"reportingCurrency":"PLN","timeZone":"","locale":"pl-PL"}"""), 400);

        problem.Extensions["code"].ShouldBe(ErrorCodes.ValidationFailed);
        var parameters = problem.Extensions["params"].ShouldBeAssignableTo<IReadOnlyDictionary<string, object>>()!;
        parameters["field"].ShouldBe("timeZone");
        parameters["rule"].ShouldBe("NotEmptyValidator");
    }

    private async Task<IActionResult> GetAsync()
    {
        await using var scope = await ScopeAsync();
        return await ActivatorUtilities.CreateInstance<GetOwnerSettingsFunction>(scope.ServiceProvider)
            .Run(new DefaultHttpContext().Request, Cancellation);
    }

    private async Task<IActionResult> PutAsync(string json)
    {
        await using var scope = await ScopeAsync();
        var request = new DefaultHttpContext().Request;
        request.ContentType = "application/json";
        request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        return await ActivatorUtilities.CreateInstance<SaveOwnerSettingsFunction>(scope.ServiceProvider).Run(request, Cancellation);
    }

    // What the worker middleware does per request: indexes once, then the owner.
    private async Task<AsyncServiceScope> ScopeAsync()
    {
        await fixture.Services.GetRequiredService<MongoIndexRunner>().EnsureCreatedAsync(Cancellation);
        var scope = fixture.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedOwnerContext>().Set(owner);
        return scope;
    }

    private static ProblemDetails Problem(IActionResult result, int status)
    {
        var objectResult = result.ShouldBeOfType<ObjectResult>();
        objectResult.StatusCode.ShouldBe(status);
        return objectResult.Value.ShouldBeOfType<ProblemDetails>();
    }
}
