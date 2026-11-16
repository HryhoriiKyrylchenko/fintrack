using FinTrack.Shared.Infrastructure.Messaging;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;
using FluentResults;
using Mediator;

namespace FinTrack.Shared.Tests.Messaging;

public class RetryOnConflictBehaviorTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Success_IsReturnedAfterOneCall()
    {
        var handler = new CountingHandler<AddTestEntry, Result>(Result.Ok());

        var result = await new RetryOnConflictBehavior<AddTestEntry, Result>().Handle(Command(), handler.Next, Cancellation);

        result.IsSuccess.ShouldBeTrue();
        handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task ConflictThenSuccess_RunsTheHandlerAgain()
    {
        var handler = new CountingHandler<AddTestEntry, Result>(Conflict(), Result.Ok());

        var result = await new RetryOnConflictBehavior<AddTestEntry, Result>().Handle(Command(), handler.Next, Cancellation);

        result.IsSuccess.ShouldBeTrue();
        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task ConflictEveryTime_StopsAfterTheLimit_AndReturnsTheConflict()
    {
        var handler = new CountingHandler<AddTestEntry, Result>(Conflict());

        var result = await new RetryOnConflictBehavior<AddTestEntry, Result>().Handle(Command(), handler.Next, Cancellation);

        result.ShouldFailWith(ErrorCodes.ConcurrencyConflict, ErrorKind.Conflict);
        handler.Calls.ShouldBe(RetryOnConflictBehavior<AddTestEntry, Result>.MaxAttempts);
    }

    [Fact]
    public async Task OtherFailure_IsNotRetried()
    {
        var handler = new CountingHandler<AddTestEntry, Result>(Result.Fail(AppError.BusinessRule("bucket.insufficient_funds")));

        var result = await new RetryOnConflictBehavior<AddTestEntry, Result>().Handle(Command(), handler.Next, Cancellation);

        result.ShouldFailWith("bucket.insufficient_funds", ErrorKind.BusinessRule);
        handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task OtherConflictCode_IsNotRetried()
    {
        var handler = new CountingHandler<AddTestEntry, Result>(Result.Fail(AppError.Conflict("bucket.already_archived")));

        await new RetryOnConflictBehavior<AddTestEntry, Result>().Handle(Command(), handler.Next, Cancellation);

        handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Exception_IsNotRetried()
    {
        var calls = 0;
        MessageHandlerDelegate<AddTestEntry, Result> next = (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("Simulated bug.");
        };

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await new RetryOnConflictBehavior<AddTestEntry, Result>().Handle(Command(), next, Cancellation));

        calls.ShouldBe(1);
    }

    [Fact]
    public async Task ResultWithValue_IsRetriedTheSameWay()
    {
        var handler = new CountingHandler<AddTestEntryWithId, Result<Guid>>(Result.Fail<Guid>(AppError.Conflict(ErrorCodes.ConcurrencyConflict)), Result.Ok(Guid.Empty));

        var result = await new RetryOnConflictBehavior<AddTestEntryWithId, Result<Guid>>().Handle(new AddTestEntryWithId("Groceries"), handler.Next, Cancellation);

        result.IsSuccess.ShouldBeTrue();
        handler.Calls.ShouldBe(2);
    }

    private static AddTestEntry Command() => new("Groceries", 120.50m);

    private static Result Conflict() => Result.Fail(AppError.Conflict(ErrorCodes.ConcurrencyConflict));
}

internal sealed record AddTestEntry(string Note, decimal Amount) : ICommand<Result>, ILedgerCommand;

internal sealed record AddTestEntryWithId(string Note) : ICommand<Result<Guid>>, ILedgerCommand;

/// <summary>Stands in for the next step of the pipeline: returns the given results in order, then the last one again.</summary>
internal sealed class CountingHandler<TMessage, TResponse>(params TResponse[] responses)
    where TMessage : IMessage
{
    public int Calls { get; private set; }

    public ValueTask<TResponse> Next(TMessage message, CancellationToken cancellationToken)
    {
        var response = responses[Math.Min(Calls, responses.Length - 1)];
        Calls++;
        return ValueTask.FromResult(response);
    }
}
