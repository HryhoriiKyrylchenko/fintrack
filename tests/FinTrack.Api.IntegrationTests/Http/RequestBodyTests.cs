using System.Text;
using FinTrack.Api.Http;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;
using FluentResults;
using Microsoft.AspNetCore.Http;

namespace FinTrack.Api.IntegrationTests.Http;

/// <summary>
/// A request body is read with the JSON contract (ARCHITECTURE 2.7). A body that does not fit is
/// a validation failure (400), never an exception (500), and the error never echoes what was sent.
/// </summary>
public class RequestBodyTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BodyThatFitsTheContract_IsRead()
    {
        var result = await ReadAsync("""{"price":{"amount":"12.30","currency":"PLN"},"occurredOn":"2026-01-15","lines":[]}""");

        result.Value.ShouldBe(new BodyProbe(Money.From(12.30m, Currency.Pln), new DateOnly(2026, 1, 15), []), new BodyProbeComparer());
    }

    [Theory]
    // An amount as a JSON number: a double on the way, so it is never accepted.
    [InlineData("""{"price":{"amount":12.30,"currency":"PLN"},"occurredOn":"2026-01-15","lines":[]}""", "price")]
    [InlineData("""{"price":{"amount":"12.345","currency":"PLN"},"occurredOn":"2026-01-15","lines":[]}""", "price")]
    [InlineData("""{"price":{"amount":"12.30","currency":"XYZ"},"occurredOn":"2026-01-15","lines":[]}""", "price")]
    [InlineData("""{"price":{"amount":"12.30","currency":"PLN"},"occurredOn":"15.01.2026","lines":[]}""", "occurredOn")]
    [InlineData("""{"price":{"amount":"12.30","currency":"PLN"},"occurredOn":"2026-01-15","lines":[{"amount":"1","currency":"PLN"},{"amount":1,"currency":"PLN"}]}""", "lines[1]")]
    [InlineData("""{"price":{"amount":"12.30","currency":"PLN"},"occurredOn":"2026-01-15","lines":[],"extra":true}""", "extra")]
    [InlineData("""{"price":""", "price")]
    [InlineData("""{"price":{"amount":"12.30","currency":"PLN"},"occurredOn":"2026-01-15","occurredOn":"2026-01-16","lines":[]}""", "occurredOn")]
    public async Task ValueThatDoesNotFit_IsAValidationFailure_AtItsField(string json, string field)
    {
        var error = (await ReadAsync(json)).ShouldFailWith(ErrorCodes.ValidationFailed, ErrorKind.Validation);

        error.Parameters["field"].ShouldBe(field);
        error.Parameters["rule"].ShouldBe(RequestBody.UnreadableRule);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"occurredOn":"2026-01-15","lines":[]}""")]
    public async Task BodyThatIsNotTheExpectedObject_IsAValidationFailure_WithoutAField(string json)
    {
        var error = (await ReadAsync(json)).ShouldFailWith(ErrorCodes.ValidationFailed, ErrorKind.Validation);

        error.Parameters["rule"].ShouldBe(RequestBody.UnreadableRule);
        error.Parameters.ShouldNotContainKey("field");
    }

    [Theory]
    [InlineData("""{"price":{"amount":"98765.432","currency":"PLN"},"occurredOn":"2026-01-15","lines":[]}""")]
    [InlineData("""{"98765.43":{"amount":"1","currency":"PLN"},"occurredOn":"2026-01-15","lines":[]}""")]
    [InlineData("""{"price":{"amount":"1","currency":"PLN"},"occurredOn":"2026-01-15","lines":[],"98765.43":1}""")]
    public async Task Failure_NeverEchoesWhatWasSent(string json)
    {
        var error = (await ReadAsync(json)).ShouldFailWith(ErrorCodes.ValidationFailed, ErrorKind.Validation);

        error.Parameters.Values.ShouldAllBe(value => !value.ToString()!.Contains("98765", StringComparison.Ordinal));
    }

    private static Task<Result<BodyProbe>> ReadAsync(string json)
    {
        var request = new DefaultHttpContext().Request;
        request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        request.ContentType = "application/json";
        return RequestBody.ReadAsync<BodyProbe>(request, Cancellation);
    }

    /// <summary>Records compare lists by reference; this compares their items.</summary>
    private sealed class BodyProbeComparer : IEqualityComparer<BodyProbe>
    {
        public bool Equals(BodyProbe? x, BodyProbe? y) =>
            x is not null && y is not null && x.Price == y.Price && x.OccurredOn == y.OccurredOn && x.Lines.SequenceEqual(y.Lines);

        public int GetHashCode(BodyProbe obj) => obj.OccurredOn.GetHashCode();
    }
}

/// <summary>A request body with the kinds of values commands will carry.</summary>
public sealed record BodyProbe(Money Price, DateOnly OccurredOn, IReadOnlyList<Money> Lines);
