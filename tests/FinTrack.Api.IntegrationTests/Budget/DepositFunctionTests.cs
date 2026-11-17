using System.Text;
using FinTrack.Api.Budget;
using FinTrack.Api.Http;
using FinTrack.Budget.Application;
using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Shared.Infrastructure.Mongo;
using FinTrack.Shared.Infrastructure.Owners;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace FinTrack.Api.IntegrationTests.Budget;

/// <summary>
/// <c>POST /api/v1/journal/deposits</c> through the Api's own container, and concurrent first deposits
/// into <c>Unallocated</c> through the mediator pipeline (conflict retry): one bucket, every deposit.
/// </summary>
public sealed class DepositFunctionTests(ApiFixture fixture)
{
    private readonly OwnerId owner = OwnerId.From(Guid.NewGuid());

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static string Today => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Deposit_Is204_AndIsInTheReplayedBalance()
    {
        await SaveSettingsAsync();

        (await DepositAsync($$"""{"id":"{{Guid.CreateVersion7()}}","amount":{"amount":"12.30","currency":"PLN"},"occurredOn":"{{Today}}","unallocated":"PLN","note":"Gift","tags":["family"]}"""))
            .ShouldBeOfType<NoContentResult>();

        var state = await StateAsync();
        var unallocated = state.Buckets.ShouldHaveSingleItem();
        state.State.BalanceOf(unallocated.BucketId).Available.ShouldBe(Money.From(12.30m, Currency.Pln));
    }

    [Fact]
    public async Task BeforeTheFirstRun_Is404SettingsNotFound() =>
        Code(await DepositAsync(Body(Guid.CreateVersion7(), "1.00", "PLN")), 404).ShouldBe("settings.not_found");

    [Theory]
    [InlineData("0.00", 400, "money.amount_not_positive")]
    [InlineData("-5.00", 400, "money.amount_not_positive")]
    public async Task AmountNotAboveZero_IsRejected(string amount, int status, string code)
    {
        await SaveSettingsAsync();

        Code(await DepositAsync(Body(Guid.CreateVersion7(), amount, "PLN")), status).ShouldBe(code);
    }

    [Fact]
    public async Task DateInTheFuture_Is422()
    {
        await SaveSettingsAsync();
        var nextWeek = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        Code(await DepositAsync($$"""{"id":"{{Guid.CreateVersion7()}}","amount":{"amount":"1.00","currency":"PLN"},"occurredOn":"{{nextWeek}}","unallocated":"PLN"}"""), 422)
            .ShouldBe("entry.date_in_future");
    }

    [Theory]
    [InlineData("""{"id":"ID","amount":{"amount":"1.00","currency":"PLN"},"occurredOn":"DAY"}""", "bucketId", "ExactlyOneTarget")]
    [InlineData("""{"id":"ID","amount":{"amount":1.00,"currency":"PLN"},"occurredOn":"DAY","unallocated":"PLN"}""", "amount", RequestBody.UnreadableRule)]
    [InlineData("""{"id":"ID","amount":{"amount":"1.001","currency":"PLN"},"occurredOn":"DAY","unallocated":"PLN"}""", "amount", RequestBody.UnreadableRule)]
    [InlineData("""{"id":"ID","amount":{"amount":"1.00","currency":"PLN"},"occurredOn":"DAY","unallocated":"PLN","tags":["a","A"]}""", "tags", "DistinctTags")]
    public async Task InvalidBody_Is400ValidationFailed_AtTheField(string template, string field, string rule)
    {
        await SaveSettingsAsync();

        var problem = Problem(await DepositAsync(template.Replace("ID", Guid.CreateVersion7().ToString(), StringComparison.Ordinal).Replace("DAY", Today, StringComparison.Ordinal)), 400);

        problem.Extensions["code"].ShouldBe(ErrorCodes.ValidationFailed);
        var parameters = problem.Extensions["params"].ShouldBeAssignableTo<IReadOnlyDictionary<string, object>>()!;
        parameters["field"].ShouldBe(field);
        parameters["rule"].ShouldBe(rule);
    }

    [Fact]
    public async Task ConcurrentFirstDeposits_IntoUnallocated_CreateOneBucket_AndKeepEveryDeposit()
    {
        await SaveSettingsAsync();
        var commands = Enumerable.Range(1, 3)
            .Select(number => new AddDeposit(Guid.CreateVersion7(), Money.From(number, Currency.Eur), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), null, Currency.Eur, null, []))
            .ToList();

        var results = await Task.WhenAll(commands.Select(SendAsync));

        results.ShouldAllBe(result => result.IsSuccess);
        var state = await StateAsync();
        var unallocated = state.Buckets.ShouldHaveSingleItem();
        unallocated.SystemRole.ShouldBe(BucketSystemRole.Unallocated);
        state.Journal.Count.ShouldBe(3);
        state.State.BalanceOf(unallocated.BucketId).Available.ShouldBe(Money.From(6m, Currency.Eur));
    }

    [Fact]
    public async Task OpeningBalanceThenSpend_Are204_AndAnOverdraftIs422WithTheDay()
    {
        await SaveSettingsAsync();
        var lastMonth = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var lastYear = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-365)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        (await OpeningBalanceAsync($$"""{"id":"{{Guid.CreateVersion7()}}","amount":{"amount":"100.00","currency":"PLN"},"occurredOn":"{{lastMonth}}","unallocated":"PLN"}"""))
            .ShouldBeOfType<NoContentResult>();
        (await SpendAsync($$"""{"id":"{{Guid.CreateVersion7()}}","amount":{"amount":"40.00","currency":"PLN"},"occurredOn":"{{Today}}","unallocated":"PLN"}"""))
            .ShouldBeOfType<NoContentResult>();

        var problem = Problem(await SpendAsync($$"""{"id":"{{Guid.CreateVersion7()}}","amount":{"amount":"1.00","currency":"PLN"},"occurredOn":"{{lastYear}}","unallocated":"PLN"}"""), 422);
        problem.Extensions["code"].ShouldBe("bucket.insufficient_funds");
        problem.Extensions["params"].ShouldBeAssignableTo<IReadOnlyDictionary<string, object>>()!["date"].ShouldBe(lastYear);

        Code(await OpeningBalanceAsync($$"""{"id":"{{Guid.CreateVersion7()}}","amount":{"amount":"1.00","currency":"PLN"},"occurredOn":"{{Today}}","unallocated":"PLN"}"""), 422)
            .ShouldBe("bucket.opening_balance_exists");

        var state = await StateAsync();
        state.State.BalanceOf(state.Buckets.ShouldHaveSingleItem().BucketId).Available.ShouldBe(Money.From(60m, Currency.Pln));
    }

    [Fact]
    public async Task Transfer_Is204_WithTheDerivedRate_AndInvalidOnesAre400()
    {
        await SaveSettingsAsync();
        await DepositAsync($$"""{"id":"{{Guid.CreateVersion7()}}","amount":{"amount":"500.00","currency":"PLN"},"occurredOn":"{{Today}}","unallocated":"PLN"}""");

        (await TransferAsync($$"""{"id":"{{Guid.CreateVersion7()}}","fromAmount":{"amount":"430.00","currency":"PLN"},"toAmount":{"amount":"100.00","currency":"EUR"},"fee":{"amount":"5.00","currency":"PLN"},"occurredOn":"{{Today}}","fromUnallocated":"PLN","toUnallocated":"EUR"}"""))
            .ShouldBeOfType<NoContentResult>();

        var state = await StateAsync();
        var transfer = state.Journal.OfType<FinTrack.Budget.Domain.Journal.Transfer>().ShouldHaveSingleItem();
        transfer.Rate.ShouldBe(0.235294117647m);
        state.State.BalanceOf(transfer.ToBucketId).Available.ShouldBe(Money.From(100m, Currency.Eur));

        Code(await TransferAsync($$"""{"id":"{{Guid.CreateVersion7()}}","fromAmount":{"amount":"1.00","currency":"PLN"},"toAmount":{"amount":"0.99","currency":"PLN"},"occurredOn":"{{Today}}","fromUnallocated":"PLN","toUnallocated":"PLN"}"""), 400)
            .ShouldBe("transfer.same_bucket");
    }

    [Fact]
    public async Task Reversal_Is204_AndThenTheBucketCanBeArchived()
    {
        await SaveSettingsAsync();
        var bucket = Guid.CreateVersion7();
        await using (var scope = await ScopeAsync())
        {
            var create = new DefaultHttpContext().Request;
            create.ContentType = "application/json";
            create.Body = new MemoryStream(Encoding.UTF8.GetBytes($$"""{"id":"{{bucket}}","name":"Holiday","currency":"PLN","kind":"Savings"}"""));
            (await ActivatorUtilities.CreateInstance<CreateBucketFunction>(scope.ServiceProvider).Run(create, Cancellation)).ShouldBeOfType<NoContentResult>();
        }

        var deposit = Guid.CreateVersion7();
        await DepositAsync($$"""{"id":"{{deposit}}","amount":{"amount":"5.00","currency":"PLN"},"occurredOn":"{{Today}}","bucketId":"{{bucket}}"}""");
        Code(await ArchiveAsync(bucket.ToString()), 422).ShouldBe("bucket.not_empty");

        (await ReverseAsync($$"""{"id":"{{Guid.CreateVersion7()}}","reversedEntryId":"{{deposit}}","occurredOn":"{{Today}}","note":"Wrong bucket"}"""))
            .ShouldBeOfType<NoContentResult>();
        Code(await ReverseAsync($$"""{"id":"{{Guid.CreateVersion7()}}","reversedEntryId":"{{deposit}}","occurredOn":"{{Today}}"}"""), 422).ShouldBe("entry.already_reversed");
        Code(await ReverseAsync($$"""{"id":"{{Guid.CreateVersion7()}}","reversedEntryId":"{{Guid.CreateVersion7()}}","occurredOn":"{{Today}}"}"""), 404).ShouldBe("entry.not_found");

        (await ArchiveAsync(bucket.ToString())).ShouldBeOfType<NoContentResult>();
        Problem(await ArchiveAsync("not-a-guid"), 400).Extensions["params"].ShouldBeAssignableTo<IReadOnlyDictionary<string, object>>()!["rule"].ShouldBe(RequestValues.RouteFormatRule);
    }

    private async Task<IActionResult> ReverseAsync(string json)
    {
        await using var scope = await ScopeAsync();
        return await ActivatorUtilities.CreateInstance<ReverseEntryFunction>(scope.ServiceProvider).Run(JsonRequest(json), Cancellation);
    }

    private async Task<IActionResult> ArchiveAsync(string id)
    {
        await using var scope = await ScopeAsync();
        return await ActivatorUtilities.CreateInstance<ArchiveBucketFunction>(scope.ServiceProvider).Run(new DefaultHttpContext().Request, id, Cancellation);
    }

    private async Task<IActionResult> TransferAsync(string json)
    {
        await using var scope = await ScopeAsync();
        return await ActivatorUtilities.CreateInstance<AddTransferFunction>(scope.ServiceProvider).Run(JsonRequest(json), Cancellation);
    }

    private async Task<IActionResult> OpeningBalanceAsync(string json)
    {
        await using var scope = await ScopeAsync();
        return await ActivatorUtilities.CreateInstance<AddOpeningBalanceFunction>(scope.ServiceProvider).Run(JsonRequest(json), Cancellation);
    }

    private async Task<IActionResult> SpendAsync(string json)
    {
        await using var scope = await ScopeAsync();
        return await ActivatorUtilities.CreateInstance<AddSpendFunction>(scope.ServiceProvider).Run(JsonRequest(json), Cancellation);
    }

    private static string Body(Guid id, string amount, string currency) =>
        $$"""{"id":"{{id}}","amount":{"amount":"{{amount}}","currency":"{{currency}}"},"occurredOn":"{{Today}}","unallocated":"{{currency}}"}""";

    private async Task SaveSettingsAsync()
    {
        await using var scope = await ScopeAsync();
        var request = JsonRequest("""{"reportingCurrency":"PLN","timeZone":"Etc/UTC","locale":"pl-PL"}""");
        (await ActivatorUtilities.CreateInstance<SaveOwnerSettingsFunction>(scope.ServiceProvider).Run(request, Cancellation)).ShouldBeOfType<NoContentResult>();
    }

    private async Task<IActionResult> DepositAsync(string json)
    {
        await using var scope = await ScopeAsync();
        return await ActivatorUtilities.CreateInstance<AddDepositFunction>(scope.ServiceProvider).Run(JsonRequest(json), Cancellation);
    }

    // Each concurrent request has its own scope, as the worker gives it; the pipeline retries conflicts.
    private async Task<FluentResults.Result> SendAsync(AddDeposit command)
    {
        await using var scope = await ScopeAsync();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(command, Cancellation);
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

    private static string Code(IActionResult result, int status) => (string)Problem(result, status).Extensions["code"]!;

    private static ProblemDetails Problem(IActionResult result, int status)
    {
        var objectResult = result.ShouldBeOfType<ObjectResult>();
        objectResult.StatusCode.ShouldBe(status);
        return objectResult.Value.ShouldBeOfType<ProblemDetails>();
    }
}
