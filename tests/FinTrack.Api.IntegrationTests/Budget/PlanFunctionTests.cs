using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FinTrack.Api.Budget;
using FinTrack.Api.Http;
using FinTrack.Budget.Application;
using FinTrack.Budget.Contracts.V1.Journal;
using FinTrack.Budget.Domain.Plans;
using FinTrack.Shared.Infrastructure.Mongo;
using FinTrack.Shared.Infrastructure.Owners;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Json;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Shared.Kernel.Time;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace FinTrack.Api.IntegrationTests.Budget;

/// <summary>
/// <c>POST /api/v1/plans</c>, <c>/plans/{id}/amount</c>, <c>/plans/{id}/end</c> and deposits and transfers linked to a plan (plan 1b
/// step 9) through the Api's own container: 4.5's example, every refusal with its status, code and month, the bodies the Api cannot
/// take, and the journal's JSON for the plan entries from the real handler's output. The owner's time zone is UTC, so the current
/// month is the month of today in UTC.
/// </summary>
public sealed class PlanFunctionTests(ApiFixture fixture)
{
    private readonly OwnerId owner = OwnerId.From(Guid.NewGuid());

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static YearMonth Current => YearMonth.FromDate(DateOnly.FromDateTime(DateTime.UtcNow));

    [Fact]
    public async Task ThePlanOf45_IsCreated_PaidInPart_Changed_AndEnded_WithEveryRefusalOnTheWay()
    {
        await SaveSettingsAsync();
        var holiday = await CreateBucketAsync("Holiday");
        var other = await CreateBucketAsync("Other");
        var plan = Guid.CreateVersion7();
        var lastMonth = Current.Previous();

        (await CreatePlanAsync($$"""{"id":"{{plan}}","bucketId":"{{holiday}}","amount":{"amount":"500","currency":"PLN"},"validFrom":"{{lastMonth}}","occurredOn":"{{Day(0)}}"}"""))
            .ShouldBeOfType<NoContentResult>();
        (await DepositAsync(holiday, "300", lastMonth.FirstDay, plan)).ShouldBeOfType<NoContentResult>();

        var standing = await StandingAsync(plan);
        standing.DueNow.ShouldBe(Pln(700m));
        standing.Shortfall.ShouldBe(Pln(200m));

        var past = Problem(await ChangeAmountAsync(plan, Change("600", lastMonth)), 422);
        past.Extensions["code"].ShouldBe("plan.past_period_changed");
        Params(past)["month"].ShouldBe(lastMonth.ToString());
        Code(await ChangeAmountAsync(plan, Change("600", lastMonth.Previous())), 400).ShouldBe("plan.months_invalid");
        Code(await ChangeAmountAsync(Guid.CreateVersion7(), Change("600", Current)), 404).ShouldBe("plan.not_found");
        Code(await DepositAsync(other, "1", DateOnly.FromDateTime(DateTime.UtcNow), plan), 422).ShouldBe("plan.bucket_mismatch");
        Code(await DepositAsync(other, "1", DateOnly.FromDateTime(DateTime.UtcNow), Guid.CreateVersion7()), 404).ShouldBe("plan.not_found");
        Code(await ArchiveAsync(holiday), 422).ShouldBe("bucket.has_active_plan");
        Code(await SetEndAsync(plan, End(lastMonth.Previous())), 400).ShouldBe("plan.months_invalid");

        (await ChangeAmountAsync(plan, Change("600", Current))).ShouldBeOfType<NoContentResult>();
        (await SetEndAsync(plan, End(lastMonth))).ShouldBeOfType<NoContentResult>();

        var ended = await StandingAsync(plan);
        ended.Status.ShouldBe(PlanStatus.Ended);
        ended.Shortfall.ShouldBe(Pln(200m));
        (await StateAsync()).Journal.Count.ShouldBe(4);
    }

    [Fact]
    public async Task ARepeatedCreate_WritesNothing_AndOtherContent_Is409()
    {
        await SaveSettingsAsync();
        var holiday = await CreateBucketAsync("Holiday");
        var id = Guid.CreateVersion7();
        var body = $$"""{"id":"{{id}}","bucketId":"{{holiday}}","amount":{"amount":"50.00","currency":"PLN"},"validFrom":"{{Current}}","occurredOn":"{{Day(0)}}"}""";

        (await CreatePlanAsync(body)).ShouldBeOfType<NoContentResult>();
        var version = (await StateAsync()).Version;
        (await CreatePlanAsync(body)).ShouldBeOfType<NoContentResult>();
        (await StateAsync()).Version.ShouldBe(version);
        Code(await CreatePlanAsync(body.Replace("50.00", "51.00", StringComparison.Ordinal)), 409).ShouldBe("entry.id_reused");
    }

    [Theory]
    [InlineData("create", """{"id":"ID","bucketId":"ID2","amount":{"amount":"1.00","currency":"PLN"},"validFrom":"2026-1","occurredOn":"DAY"}""", "validFrom", RequestBody.UnreadableRule)]
    [InlineData("create", """{"id":"ID","bucketId":"ID2","amount":{"amount":"1.00","currency":"PLN"},"validFrom":"2026-01-01","occurredOn":"DAY"}""", "validFrom", RequestBody.UnreadableRule)]
    [InlineData("create", """{"id":"ID","bucketId":"ID2","amount":{"amount":"1.00","currency":"PLN"},"validFrom":202601,"occurredOn":"DAY"}""", "validFrom", RequestBody.UnreadableRule)]
    [InlineData("create", """{"id":"ID","bucketId":"ID2","amount":{"amount":1,"currency":"PLN"},"validFrom":"2026-01","occurredOn":"DAY"}""", "amount", RequestBody.UnreadableRule)]
    [InlineData("amount", """{"id":"ID","amount":{"amount":"1.00","currency":"PLN"},"from":"2026/02","occurredOn":"DAY"}""", "from", RequestBody.UnreadableRule)]
    [InlineData("end", """{"id":"ID","validTo":"13-2026","occurredOn":"DAY"}""", "validTo", RequestBody.UnreadableRule)]
    [InlineData("end", """{"id":"ID","occurredOn":"DAY","extra":1}""", null, RequestBody.UnreadableRule)]
    public async Task InvalidBody_Is400ValidationFailed_AtTheField(string kind, string template, string? field, string rule)
    {
        await SaveSettingsAsync();
        var json = template.Replace("ID2", Guid.CreateVersion7().ToString(), StringComparison.Ordinal)
            .Replace("ID", Guid.CreateVersion7().ToString(), StringComparison.Ordinal).Replace("DAY", Day(0), StringComparison.Ordinal);

        var result = kind switch
        {
            "create" => await CreatePlanAsync(json),
            "amount" => await ChangeAmountAsync(Guid.CreateVersion7(), json),
            _ => await SetEndAsync(Guid.CreateVersion7(), json),
        };

        var problem = Problem(result, 400);
        problem.Extensions["code"].ShouldBe(ErrorCodes.ValidationFailed);
        Params(problem)["rule"].ShouldBe(rule);
        if (field is not null)
        {
            Params(problem)["field"].ShouldBe(field);
        }

        (await StateAsync()).Journal.ShouldBeEmpty();
    }

    [Fact]
    public async Task APlanIdThatIsNotAGuid_Is400RouteFormat()
    {
        await SaveSettingsAsync();

        foreach (var result in new[] { await ChangeAmountAsync("nope", Change("1", Current)), await SetEndAsync("nope", End(null)) })
        {
            var problem = Problem(result, 400);
            Params(problem)["field"].ShouldBe("id");
            Params(problem)["rule"].ShouldBe("RouteFormat");
        }
    }

    [Fact]
    public async Task AnEndBeforeTheStart_Is400MonthsInvalid()
    {
        await SaveSettingsAsync();
        var holiday = await CreateBucketAsync("Holiday");

        Code(await CreatePlanAsync($$"""{"id":"{{Guid.CreateVersion7()}}","bucketId":"{{holiday}}","amount":{"amount":"1","currency":"PLN"},"validFrom":"{{Current}}","validTo":"{{Current.Previous()}}","occurredOn":"{{Day(0)}}"}"""), 400)
            .ShouldBe("plan.months_invalid");
    }

    [Fact]
    public async Task TheJournal_ShowsThePlanEntries_InTheV1Shape()
    {
        await SaveSettingsAsync();
        var holiday = await CreateBucketAsync("Holiday");
        var cushion = await CreateBucketAsync("Cushion");
        var plan = Guid.CreateVersion7();
        var end = Current.AddMonths(11);
        (await CreatePlanAsync($$"""{"id":"{{plan}}","bucketId":"{{holiday}}","amount":{"amount":"500","currency":"PLN"},"validFrom":"{{Current}}","validTo":"{{end}}","occurredOn":"{{Day(0)}}","note":"Holiday fund"}"""))
            .ShouldBeOfType<NoContentResult>();
        var change = Guid.CreateVersion7();
        (await ChangeAmountAsync(plan, $$"""{"id":"{{change}}","amount":{"amount":"600","currency":"PLN"},"from":"{{Current.Next()}}","occurredOn":"{{Day(0)}}"}""")).ShouldBeOfType<NoContentResult>();
        var ended = Guid.CreateVersion7();
        (await SetEndAsync(plan, $$"""{"id":"{{ended}}","occurredOn":"{{Day(0)}}"}""")).ShouldBeOfType<NoContentResult>();
        (await DepositAsync(cushion, "1000", DateOnly.FromDateTime(DateTime.UtcNow), null)).ShouldBeOfType<NoContentResult>();
        var transfer = Guid.CreateVersion7();
        (await TransferAsync($$"""{"id":"{{transfer}}","fromAmount":{"amount":"250","currency":"PLN"},"toAmount":{"amount":"250","currency":"PLN"},"occurredOn":"{{Day(0)}}","fromBucketId":"{{cushion}}","toBucketId":"{{holiday}}","planId":"{{plan}}"}"""))
            .ShouldBeOfType<NoContentResult>();

        var page = (await JournalAsync($"?bucketId={holiday}")).ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<JournalPageDto>();
        var day = Day(0);

        page.Entries.Select(entry => entry.Id).ShouldBe([transfer, ended, change, plan]);
        Json(page.Entries[0]).ShouldBe(
            "{\"id\":\"" + transfer + "\",\"type\":\"Transfer\",\"occurredOn\":\"" + day + "\",\"note\":null,\"tags\":[],\"groupId\":null,\"source\":\"Manual\",\"externalId\":null,"
            + "\"bucketId\":null,\"amount\":null,\"fromBucketId\":\"" + cushion + "\",\"toBucketId\":\"" + holiday + "\",\"fromAmount\":{\"amount\":\"250.00\",\"currency\":\"PLN\"},"
            + "\"toAmount\":{\"amount\":\"250.00\",\"currency\":\"PLN\"},\"fee\":null,\"rate\":\"1\",\"reversedEntryId\":null,\"reversedBy\":null,\"direction\":null,\"loanId\":null,"
            + "\"planId\":\"" + plan + "\",\"fromMonth\":null,\"toMonth\":null}");
        Json(page.Entries[1]).ShouldBe(
            "{\"id\":\"" + ended + "\",\"type\":\"PlanEndChanged\",\"occurredOn\":\"" + day + "\",\"note\":null,\"tags\":[],\"groupId\":null,\"source\":\"Manual\",\"externalId\":null,"
            + "\"bucketId\":\"" + holiday + "\",\"amount\":null,\"fromBucketId\":null,\"toBucketId\":null,\"fromAmount\":null,\"toAmount\":null,"
            + "\"fee\":null,\"rate\":null,\"reversedEntryId\":null,\"reversedBy\":null,\"direction\":null,\"loanId\":null,\"planId\":\"" + plan + "\",\"fromMonth\":null,\"toMonth\":null}");
        Json(page.Entries[2]).ShouldBe(
            "{\"id\":\"" + change + "\",\"type\":\"PlanAmountChanged\",\"occurredOn\":\"" + day + "\",\"note\":null,\"tags\":[],\"groupId\":null,\"source\":\"Manual\",\"externalId\":null,"
            + "\"bucketId\":\"" + holiday + "\",\"amount\":{\"amount\":\"600.00\",\"currency\":\"PLN\"},\"fromBucketId\":null,\"toBucketId\":null,\"fromAmount\":null,\"toAmount\":null,"
            + "\"fee\":null,\"rate\":null,\"reversedEntryId\":null,\"reversedBy\":null,\"direction\":null,\"loanId\":null,\"planId\":\"" + plan + "\",\"fromMonth\":\"" + Current.Next() + "\",\"toMonth\":null}");
        Json(page.Entries[3]).ShouldBe(
            "{\"id\":\"" + plan + "\",\"type\":\"PlanCreated\",\"occurredOn\":\"" + day + "\",\"note\":\"Holiday fund\",\"tags\":[],\"groupId\":null,\"source\":\"Manual\",\"externalId\":null,"
            + "\"bucketId\":\"" + holiday + "\",\"amount\":{\"amount\":\"500.00\",\"currency\":\"PLN\"},\"fromBucketId\":null,\"toBucketId\":null,\"fromAmount\":null,\"toAmount\":null,"
            + "\"fee\":null,\"rate\":null,\"reversedEntryId\":null,\"reversedBy\":null,\"direction\":null,\"loanId\":null,\"planId\":\"" + plan + "\",\"fromMonth\":\"" + Current + "\",\"toMonth\":\"" + end + "\"}");
    }

    private static Money Pln(decimal amount) => Money.From(amount, Currency.Pln);

    private static string Day(int offset) => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(offset)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Change(string amount, YearMonth from) =>
        $$"""{"id":"{{Guid.CreateVersion7()}}","amount":{"amount":"{{amount}}","currency":"PLN"},"from":"{{from}}","occurredOn":"{{Day(0)}}"}""";

    private static string End(YearMonth? to) =>
        $$"""{"id":"{{Guid.CreateVersion7()}}","occurredOn":"{{Day(0)}}"{{(to is { } month ? $",\"validTo\":\"{month}\"" : string.Empty)}}}""";

    // The entry as JSON, without the instant it was recorded (set by the clock).
    private static string Json(JournalEntryDto entry)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(entry, FinTrackJson.Options))!.AsObject();
        node.Remove("recordedAt");
        return node.ToJsonString();
    }

    private async Task<PlanStanding> StandingAsync(Guid plan)
    {
        var state = (await StateAsync()).StateAsOf(DateOnly.FromDateTime(DateTime.UtcNow));
        return state.StandingOf(state.FindPlan(PlanId.From(plan))!);
    }

    private async Task SaveSettingsAsync()
    {
        await using var scope = await ScopeAsync();
        var request = JsonRequest("""{"reportingCurrency":"PLN","timeZone":"Etc/UTC","locale":"pl-PL"}""");
        (await ActivatorUtilities.CreateInstance<SaveOwnerSettingsFunction>(scope.ServiceProvider).Run(request, Cancellation)).ShouldBeOfType<NoContentResult>();
    }

    private async Task<Guid> CreateBucketAsync(string name)
    {
        var id = Guid.CreateVersion7();
        await using var scope = await ScopeAsync();
        var request = JsonRequest($$"""{"id":"{{id}}","name":"{{name}}","currency":"PLN","kind":"Savings"}""");
        (await ActivatorUtilities.CreateInstance<CreateBucketFunction>(scope.ServiceProvider).Run(request, Cancellation)).ShouldBeOfType<NoContentResult>();
        return id;
    }

    private async Task<IActionResult> DepositAsync(Guid bucket, string amount, DateOnly day, Guid? plan)
    {
        await using var scope = await ScopeAsync();
        var planPart = plan is { } id ? $",\"planId\":\"{id}\"" : string.Empty;
        var request = JsonRequest($$"""{"id":"{{Guid.CreateVersion7()}}","amount":{"amount":"{{amount}}","currency":"PLN"},"occurredOn":"{{day:yyyy-MM-dd}}","bucketId":"{{bucket}}"{{planPart}}}""");
        return await ActivatorUtilities.CreateInstance<AddDepositFunction>(scope.ServiceProvider).Run(request, Cancellation);
    }

    private async Task<IActionResult> TransferAsync(string json)
    {
        await using var scope = await ScopeAsync();
        return await ActivatorUtilities.CreateInstance<AddTransferFunction>(scope.ServiceProvider).Run(JsonRequest(json), Cancellation);
    }

    private async Task<IActionResult> CreatePlanAsync(string json)
    {
        await using var scope = await ScopeAsync();
        return await ActivatorUtilities.CreateInstance<CreatePlanFunction>(scope.ServiceProvider).Run(JsonRequest(json), Cancellation);
    }

    private Task<IActionResult> ChangeAmountAsync(Guid plan, string json) => ChangeAmountAsync(plan.ToString(), json);

    private async Task<IActionResult> ChangeAmountAsync(string plan, string json)
    {
        await using var scope = await ScopeAsync();
        return await ActivatorUtilities.CreateInstance<ChangePlanAmountFunction>(scope.ServiceProvider).Run(JsonRequest(json), plan, Cancellation);
    }

    private Task<IActionResult> SetEndAsync(Guid plan, string json) => SetEndAsync(plan.ToString(), json);

    private async Task<IActionResult> SetEndAsync(string plan, string json)
    {
        await using var scope = await ScopeAsync();
        return await ActivatorUtilities.CreateInstance<SetPlanEndFunction>(scope.ServiceProvider).Run(JsonRequest(json), plan, Cancellation);
    }

    private async Task<IActionResult> ArchiveAsync(Guid bucket)
    {
        await using var scope = await ScopeAsync();
        return await ActivatorUtilities.CreateInstance<ArchiveBucketFunction>(scope.ServiceProvider).Run(new DefaultHttpContext().Request, bucket.ToString(), Cancellation);
    }

    private async Task<IActionResult> JournalAsync(string query)
    {
        await using var scope = await ScopeAsync();
        var request = new DefaultHttpContext().Request;
        request.QueryString = new QueryString(query);
        return await ActivatorUtilities.CreateInstance<GetJournalFunction>(scope.ServiceProvider).Run(request, Cancellation);
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

    private static IReadOnlyDictionary<string, object> Params(ProblemDetails problem) =>
        problem.Extensions["params"].ShouldBeAssignableTo<IReadOnlyDictionary<string, object>>()!;

    private static string Code(IActionResult result, int status) => (string)Problem(result, status).Extensions["code"]!;

    private static ProblemDetails Problem(IActionResult result, int status)
    {
        var objectResult = result.ShouldBeOfType<ObjectResult>();
        objectResult.StatusCode.ShouldBe(status);
        return objectResult.Value.ShouldBeOfType<ProblemDetails>();
    }
}
