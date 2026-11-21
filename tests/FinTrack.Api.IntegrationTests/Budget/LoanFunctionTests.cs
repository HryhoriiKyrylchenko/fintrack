using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FinTrack.Api.Budget;
using FinTrack.Api.Http;
using FinTrack.Budget.Application;
using FinTrack.Budget.Contracts.V1.Journal;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Loans;
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
/// <c>POST /api/v1/journal/borrows</c> and <c>/repayments</c> (plan 1b step 5) through the Api's own container: a loan opened,
/// repaid and closed; the refusals with their status, code and date; the bodies the Api cannot take; archiving a lender with an
/// open loan; and the journal's JSON for both types from the real handler's output.
/// </summary>
public sealed class LoanFunctionTests(ApiFixture fixture)
{
    private readonly OwnerId owner = OwnerId.From(Guid.NewGuid());

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ALoan_IsOpened_Repaid_AndClosed_WithEveryRefusalOnTheWay()
    {
        await SaveSettingsAsync();
        var cushion = await CreateBucketAsync("Cushion");
        var car = await CreateBucketAsync("Car");
        (await DepositAsync(cushion, "500.00", Day(-10))).ShouldBeOfType<NoContentResult>();
        var loan = Guid.CreateVersion7();

        (await BorrowAsync($$"""{"id":"{{loan}}","lenderBucketId":"{{cushion}}","principal":{"amount":"200","currency":"PLN"},"occurredOn":"{{Day(-5)}}","toBucketId":"{{car}}"}"""))
            .ShouldBeOfType<NoContentResult>();
        Code(await ArchiveAsync(cushion), 422).ShouldBe("bucket.has_open_loan");

        var tooMuch = Problem(await RepayAsync(Repayment(loan, "200.01", Day(-1), car)), 422);
        tooMuch.Extensions["code"].ShouldBe("loan.repayment_exceeds_outstanding");
        Params(tooMuch)["date"].ShouldBe(Day(-1));
        Params(Problem(await RepayAsync(Repayment(loan, "1.00", Day(-7), null)), 422))["date"].ShouldBe(Day(-7));
        Code(await RepayAsync(Repayment(loan, "1.00", Day(-1), cushion)), 422).ShouldBe("repayment.from_lender");
        Code(await RepayAsync(Repayment(Guid.CreateVersion7(), "1.00", Day(-1), null)), 404).ShouldBe("loan.not_found");

        (await RepayAsync(Repayment(loan, "150", Day(-3), car))).ShouldBeOfType<NoContentResult>();
        (await RepayAsync(Repayment(loan, "50", Day(-1), null))).ShouldBeOfType<NoContentResult>();
        Code(await RepayAsync(Repayment(loan, "1.00", Day(0), null)), 422).ShouldBe("loan.closed");

        var state = (await StateAsync()).StateAsOf(DateOnly.FromDateTime(DateTime.UtcNow));
        state.FindLoan(LoanId.From(loan))!.Status.ShouldBe(LoanStatus.Closed);
        state.BalanceOf(BucketId.From(cushion)).Available.ShouldBe(Money.From(500m, Currency.Pln));
        state.BalanceOf(BucketId.From(car)).NetContributed.ShouldBe(Money.From(50m, Currency.Pln));
    }

    [Fact]
    public async Task BorrowingFromAnInvestmentBucket_OrMoreThanIsThere_IsRefused()
    {
        await SaveSettingsAsync();
        var cushion = await CreateBucketAsync("Cushion");
        await using (var scope = await ScopeAsync())
        {
            var request = JsonRequest($$"""{"id":"{{Guid.CreateVersion7()}}","name":"Coins","currency":"PLN","kind":"Investment","profile":"Crypto"}""");
            (await ActivatorUtilities.CreateInstance<CreateBucketFunction>(scope.ServiceProvider).Run(request, Cancellation)).ShouldBeOfType<NoContentResult>();
        }

        var coins = (await StateAsync()).Buckets.Single(bucket => bucket.Kind == BucketKind.Investment).Id;
        Code(await BorrowAsync(Borrow(coins, "1.00", Day(0), null)), 422).ShouldBe("bucket.borrow_not_allowed");
        var problem = Problem(await BorrowAsync(Borrow(cushion, "1.00", Day(-1), null)), 422);
        problem.Extensions["code"].ShouldBe("bucket.insufficient_funds");
        Params(problem)["date"].ShouldBe(Day(-1));
        Code(await BorrowAsync(Borrow(cushion, "1.00", Day(0), cushion)), 400).ShouldBe("borrow.same_bucket");
        (await StateAsync()).Journal.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("borrow", """{"id":"ID","lenderBucketId":"ID2","principal":{"amount":"1.00","currency":"PLN"},"occurredOn":"DAY","toBucketId":"ID3","toUnallocated":"PLN"}""", "toBucketId", "AtMostOneTarget")]
    [InlineData("repay", """{"id":"ID","loanId":"ID2","amount":{"amount":"1.00","currency":"PLN"},"occurredOn":"DAY","fromBucketId":"ID3","fromUnallocated":"PLN"}""", "fromBucketId", "AtMostOneTarget")]
    [InlineData("borrow", """{"id":"ID","principal":{"amount":"1.00","currency":"PLN"},"occurredOn":"DAY"}""", null, RequestBody.UnreadableRule)]
    [InlineData("repay", """{"id":"ID","loanId":"ID2","amount":{"amount":1,"currency":"PLN"},"occurredOn":"DAY"}""", "amount", RequestBody.UnreadableRule)]
    public async Task InvalidBody_Is400ValidationFailed_AtTheField(string kind, string template, string? field, string rule)
    {
        await SaveSettingsAsync();
        var json = template.Replace("ID3", Guid.CreateVersion7().ToString(), StringComparison.Ordinal).Replace("ID2", Guid.CreateVersion7().ToString(), StringComparison.Ordinal)
            .Replace("ID", Guid.CreateVersion7().ToString(), StringComparison.Ordinal).Replace("DAY", Day(0), StringComparison.Ordinal);

        var problem = Problem(kind == "borrow" ? await BorrowAsync(json) : await RepayAsync(json), 400);

        problem.Extensions["code"].ShouldBe(ErrorCodes.ValidationFailed);
        Params(problem)["rule"].ShouldBe(rule);
        if (field is not null)
        {
            Params(problem)["field"].ShouldBe(field);
        }
    }

    [Fact]
    public async Task TheJournal_ShowsBorrowAndRepaymentWithTheirLoan_InTheV1Shape()
    {
        await SaveSettingsAsync();
        var cushion = await CreateBucketAsync("Cushion");
        var car = await CreateBucketAsync("Car");
        (await DepositAsync(cushion, "500.00", Day(-10))).ShouldBeOfType<NoContentResult>();
        var loan = Guid.CreateVersion7();
        (await BorrowAsync($$"""{"id":"{{loan}}","lenderBucketId":"{{cushion}}","principal":{"amount":"200","currency":"PLN"},"occurredOn":"{{Day(-5)}}","toBucketId":"{{car}}","note":"Repair"}"""))
            .ShouldBeOfType<NoContentResult>();
        var repayment = Guid.CreateVersion7();
        (await RepayAsync($$"""{"id":"{{repayment}}","loanId":"{{loan}}","amount":{"amount":"20","currency":"PLN"},"occurredOn":"{{Day(-1)}}"}""")).ShouldBeOfType<NoContentResult>();

        var page = (await JournalAsync()).ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<JournalPageDto>();

        Json(page.Entries[0]).ShouldBe(
            "{\"id\":\"" + repayment + "\",\"type\":\"Repayment\",\"occurredOn\":\"" + Day(-1) + "\",\"note\":null,\"tags\":[],\"groupId\":null,\"source\":\"Manual\",\"externalId\":null,"
            + "\"bucketId\":null,\"amount\":{\"amount\":\"20.00\",\"currency\":\"PLN\"},\"fromBucketId\":null,\"toBucketId\":\"" + cushion + "\",\"fromAmount\":null,\"toAmount\":null,"
            + "\"fee\":null,\"rate\":null,\"reversedEntryId\":null,\"reversedBy\":null,\"direction\":null,\"loanId\":\"" + loan + "\"}");
        Json(page.Entries[1]).ShouldBe(
            "{\"id\":\"" + loan + "\",\"type\":\"Borrow\",\"occurredOn\":\"" + Day(-5) + "\",\"note\":\"Repair\",\"tags\":[],\"groupId\":null,\"source\":\"Manual\",\"externalId\":null,"
            + "\"bucketId\":null,\"amount\":{\"amount\":\"200.00\",\"currency\":\"PLN\"},\"fromBucketId\":\"" + cushion + "\",\"toBucketId\":\"" + car + "\",\"fromAmount\":null,\"toAmount\":null,"
            + "\"fee\":null,\"rate\":null,\"reversedEntryId\":null,\"reversedBy\":null,\"direction\":null,\"loanId\":\"" + loan + "\"}");
    }

    // The entry as JSON, without the instant it was recorded (set by the clock).
    private static string Json(JournalEntryDto entry)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(entry, FinTrackJson.Options))!.AsObject();
        node.Remove("recordedAt");
        return node.ToJsonString();
    }

    private static string Day(int offset) => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(offset)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Borrow(Guid lender, string amount, string day, Guid? to) =>
        $$"""{"id":"{{Guid.CreateVersion7()}}","lenderBucketId":"{{lender}}","principal":{"amount":"{{amount}}","currency":"PLN"},"occurredOn":"{{day}}"{{(to is { } bucket ? $",\"toBucketId\":\"{bucket}\"" : string.Empty)}}}""";

    private static string Repayment(Guid loan, string amount, string day, Guid? from) =>
        $$"""{"id":"{{Guid.CreateVersion7()}}","loanId":"{{loan}}","amount":{"amount":"{{amount}}","currency":"PLN"},"occurredOn":"{{day}}"{{(from is { } bucket ? $",\"fromBucketId\":\"{bucket}\"" : string.Empty)}}}""";

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

    private async Task<IActionResult> DepositAsync(Guid bucket, string amount, string day)
    {
        await using var scope = await ScopeAsync();
        var request = JsonRequest($$"""{"id":"{{Guid.CreateVersion7()}}","amount":{"amount":"{{amount}}","currency":"PLN"},"occurredOn":"{{day}}","bucketId":"{{bucket}}"}""");
        return await ActivatorUtilities.CreateInstance<AddDepositFunction>(scope.ServiceProvider).Run(request, Cancellation);
    }

    private async Task<IActionResult> BorrowAsync(string json)
    {
        await using var scope = await ScopeAsync();
        return await ActivatorUtilities.CreateInstance<AddBorrowFunction>(scope.ServiceProvider).Run(JsonRequest(json), Cancellation);
    }

    private async Task<IActionResult> RepayAsync(string json)
    {
        await using var scope = await ScopeAsync();
        return await ActivatorUtilities.CreateInstance<AddRepaymentFunction>(scope.ServiceProvider).Run(JsonRequest(json), Cancellation);
    }

    private async Task<IActionResult> ArchiveAsync(Guid bucket)
    {
        await using var scope = await ScopeAsync();
        var request = new DefaultHttpContext().Request;
        return await ActivatorUtilities.CreateInstance<ArchiveBucketFunction>(scope.ServiceProvider).Run(request, bucket.ToString(), Cancellation);
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
