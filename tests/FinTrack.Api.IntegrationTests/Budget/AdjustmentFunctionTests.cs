using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FinTrack.Api.Budget;
using FinTrack.Api.Http;
using FinTrack.Budget.Application;
using FinTrack.Budget.Contracts.V1.Journal;
using FinTrack.Shared.Infrastructure.Mongo;
using FinTrack.Shared.Infrastructure.Owners;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Json;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace FinTrack.Api.IntegrationTests.Budget;

/// <summary>
/// <c>POST /api/v1/journal/adjustments</c> (plan 1b B14) through the Api's own container: both directions, a decrease
/// below zero refused with the day, the required note, a direction that is not a name, and the journal's JSON for an
/// adjustment from the real handler's output.
/// </summary>
public sealed class AdjustmentFunctionTests(ApiFixture fixture)
{
    private readonly OwnerId owner = OwnerId.From(Guid.NewGuid());

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static string Yesterday => Day(-1);

    [Fact]
    public async Task AnIncreaseAndADecrease_Are204_AndChangeTheBalance()
    {
        await SaveSettingsAsync();

        (await AdjustAsync(Body(Guid.CreateVersion7(), "Increase", "50", Day(-3)))).ShouldBeOfType<NoContentResult>();
        (await AdjustAsync(Body(Guid.CreateVersion7(), "Decrease", "20.25", Yesterday))).ShouldBeOfType<NoContentResult>();

        var state = await StateAsync();
        var balance = state.StateAsOf(DateOnly.FromDateTime(DateTime.UtcNow)).BalanceOf(state.Buckets.ShouldHaveSingleItem().BucketId);
        balance.NetContributed.ShouldBe(Money.From(29.75m, Currency.Pln));
        balance.Deposited.ShouldBe(Money.Zero(Currency.Pln));
    }

    [Fact]
    public async Task ADecreaseBelowZero_Is422InsufficientFunds_WithTheDay_AndWritesNothing()
    {
        await SaveSettingsAsync();
        (await AdjustAsync(Body(Guid.CreateVersion7(), "Increase", "10.00", Day(-3)))).ShouldBeOfType<NoContentResult>();

        var problem = Problem(await AdjustAsync(Body(Guid.CreateVersion7(), "Decrease", "10.01", Day(-2))), 422);

        problem.Extensions["code"].ShouldBe("bucket.insufficient_funds");
        problem.Extensions["params"].ShouldBeAssignableTo<IReadOnlyDictionary<string, object>>()!["date"].ShouldBe(Day(-2));
        (await StateAsync()).Journal.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("""{"id":"ID","amount":{"amount":"1.00","currency":"PLN"},"direction":"Increase","occurredOn":"DAY","unallocated":"PLN"}""", "note", "NotEmptyValidator")]
    [InlineData("""{"id":"ID","amount":{"amount":"1.00","currency":"PLN"},"direction":"Increase","occurredOn":"DAY","unallocated":"PLN","note":""}""", "note", "NotEmptyValidator")]
    [InlineData("""{"id":"ID","amount":{"amount":"1.00","currency":"PLN"},"direction":"Increase","occurredOn":"DAY","unallocated":"PLN","note":null}""", "note", "NotEmptyValidator")]
    [InlineData("""{"id":"ID","amount":{"amount":"1.00","currency":"PLN"},"direction":"increase","occurredOn":"DAY","unallocated":"PLN","note":"Recount"}""", "direction", RequestBody.UnreadableRule)]
    [InlineData("""{"id":"ID","amount":{"amount":"1.00","currency":"PLN"},"direction":1,"occurredOn":"DAY","unallocated":"PLN","note":"Recount"}""", "direction", RequestBody.UnreadableRule)]
    [InlineData("""{"id":"ID","amount":{"amount":"1.00","currency":"PLN"},"occurredOn":"DAY","unallocated":"PLN","note":"Recount"}""", null, RequestBody.UnreadableRule)]
    [InlineData("""{"id":"ID","amount":{"amount":"1.00","currency":"PLN"},"direction":"Increase","occurredOn":"DAY","note":"Recount"}""", "bucketId", "ExactlyOneTarget")]
    public async Task InvalidBody_Is400ValidationFailed_AtTheField_AndWritesNothing(string template, string? field, string rule)
    {
        await SaveSettingsAsync();

        var problem = Problem(await AdjustAsync(template.Replace("ID", Guid.CreateVersion7().ToString(), StringComparison.Ordinal).Replace("DAY", Yesterday, StringComparison.Ordinal)), 400);

        problem.Extensions["code"].ShouldBe(ErrorCodes.ValidationFailed);
        var parameters = problem.Extensions["params"].ShouldBeAssignableTo<IReadOnlyDictionary<string, object>>()!;
        parameters["rule"].ShouldBe(rule);
        if (field is not null)
        {
            parameters["field"].ShouldBe(field);
        }

        (await StateAsync()).Journal.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheJournal_ShowsTheAdjustmentWithItsDirection_InTheV1Shape()
    {
        await SaveSettingsAsync();
        (await AdjustAsync(Body(Guid.CreateVersion7(), "Increase", "10", Day(-5)))).ShouldBeOfType<NoContentResult>();
        var id = Guid.CreateVersion7();
        (await AdjustAsync($$"""{"id":"{{id}}","amount":{"amount":"7.5","currency":"PLN"},"direction":"Decrease","occurredOn":"{{Day(-2)}}","unallocated":"PLN","note":"Counted the cash box","tags":["cash"]}"""))
            .ShouldBeOfType<NoContentResult>();

        var page = (await JournalAsync()).ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<JournalPageDto>();
        var bucket = (await StateAsync()).Buckets.ShouldHaveSingleItem().Id;
        var entry = JsonNode.Parse(JsonSerializer.Serialize(page.Entries[0], FinTrackJson.Options))!.AsObject();
        entry.Remove("recordedAt");

        entry.ToJsonString().ShouldBe(
            "{\"id\":\"" + id + "\",\"type\":\"Adjustment\",\"occurredOn\":\"" + Day(-2) + "\",\"note\":\"Counted the cash box\",\"tags\":[\"cash\"],"
            + "\"groupId\":null,\"source\":\"Manual\",\"externalId\":null,\"bucketId\":\"" + bucket + "\",\"amount\":{\"amount\":\"7.50\",\"currency\":\"PLN\"},"
            + "\"fromBucketId\":null,\"toBucketId\":null,\"fromAmount\":null,\"toAmount\":null,\"fee\":null,\"rate\":null,"
            + "\"reversedEntryId\":null,\"reversedBy\":null,\"direction\":\"Decrease\",\"loanId\":null,\"planId\":null,\"fromMonth\":null,\"toMonth\":null}");
        page.Entries[1].Direction.ShouldBe(AdjustmentDirection.Increase);
    }

    private static string Day(int offset) => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(offset)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Body(Guid id, string direction, string amount, string day) =>
        $$"""{"id":"{{id}}","amount":{"amount":"{{amount}}","currency":"PLN"},"direction":"{{direction}}","occurredOn":"{{day}}","unallocated":"PLN","note":"Recount"}""";

    private async Task SaveSettingsAsync()
    {
        await using var scope = await ScopeAsync();
        var request = JsonRequest("""{"reportingCurrency":"PLN","timeZone":"Etc/UTC","locale":"pl-PL"}""");
        (await ActivatorUtilities.CreateInstance<SaveOwnerSettingsFunction>(scope.ServiceProvider).Run(request, Cancellation)).ShouldBeOfType<NoContentResult>();
    }

    private async Task<IActionResult> AdjustAsync(string json)
    {
        await using var scope = await ScopeAsync();
        return await ActivatorUtilities.CreateInstance<AddAdjustmentFunction>(scope.ServiceProvider).Run(JsonRequest(json), Cancellation);
    }

    private async Task<IActionResult> JournalAsync()
    {
        await using var scope = await ScopeAsync();
        return await ActivatorUtilities.CreateInstance<GetJournalFunction>(scope.ServiceProvider).Run(new DefaultHttpContext().Request, Cancellation);
    }

    private async Task<BudgetSnapshot> StateAsync()
    {
        await using var scope = await ScopeAsync();
        return (await scope.ServiceProvider.GetRequiredService<BudgetStateLoader>().LoadAsync(Cancellation)).Value;
    }

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

    private static ProblemDetails Problem(IActionResult result, int status)
    {
        var objectResult = result.ShouldBeOfType<ObjectResult>();
        objectResult.StatusCode.ShouldBe(status);
        return objectResult.Value.ShouldBeOfType<ProblemDetails>();
    }
}
