using FinTrack.Shared.Infrastructure.Messaging;
using FinTrack.Shared.Kernel.Results;
using FluentResults;
using FluentValidation;
using Mediator;

namespace FinTrack.Shared.Tests.Messaging;

public class ValidationBehaviorTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithoutValidators_CallsTheHandler()
    {
        var handler = new CountingHandler<AddTestEntry, Result>(Result.Ok());

        var result = await new ValidationBehavior<AddTestEntry, Result>([]).Handle(Valid(), handler.Next, Cancellation);

        result.IsSuccess.ShouldBeTrue();
        handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task ValidMessage_CallsTheHandlerOnce()
    {
        var handler = new CountingHandler<AddTestEntry, Result>(Result.Ok());

        var result = await Behavior().Handle(Valid(), handler.Next, Cancellation);

        result.IsSuccess.ShouldBeTrue();
        handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task InvalidMessage_FailsWithOneErrorPerField_AndNeverCallsTheHandler()
    {
        var handler = new CountingHandler<AddTestEntry, Result>(Result.Ok());

        var result = await Behavior().Handle(Valid() with { Note = "", Amount = -12.34m }, handler.Next, Cancellation);

        handler.Calls.ShouldBe(0);
        var errors = AppErrors(result);
        errors.ShouldAllBe(error => error.Code == ErrorCodes.ValidationFailed && error.Kind == ErrorKind.Validation);
        errors.Select(error => (error.Parameters["field"], error.Parameters["rule"])).ShouldBe(
            [("note", "NotEmptyValidator"), ("amount", "GreaterThanValidator")],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Errors_NeverContainTheValueThatWasSent()
    {
        var handler = new CountingHandler<AddTestEntry, Result>(Result.Ok());

        var result = await Behavior().Handle(Valid() with { Amount = -98765.43m }, handler.Next, Cancellation);

        AppErrors(result)
            .SelectMany(error => error.Parameters.Values)
            .ShouldAllBe(value => !(value.ToString() ?? string.Empty).Contains("98765", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NestedFields_AreNamedWithCamelCasePaths()
    {
        var handler = new CountingHandler<SplitTestPayment, Result>(Result.Ok());
        var message = new SplitTestPayment([new SplitLine(10m), new SplitLine(0m)]);

        var result = await new ValidationBehavior<SplitTestPayment, Result>([new SplitTestPaymentValidator()]).Handle(message, handler.Next, Cancellation);

        AppErrors(result).ShouldHaveSingleItem().Parameters["field"].ShouldBe("lines[1].amount");
    }

    [Fact]
    public async Task AllValidatorsRun()
    {
        var handler = new CountingHandler<AddTestEntry, Result>(Result.Ok());
        var behavior = new ValidationBehavior<AddTestEntry, Result>([new AddTestEntryValidator(), new NoteLengthValidator()]);

        var result = await behavior.Handle(Valid() with { Note = new string('x', 300), Amount = 0m }, handler.Next, Cancellation);

        AppErrors(result).Select(error => error.Parameters["field"]).ShouldBe(["amount", "note"], ignoreOrder: true);
    }

    [Fact]
    public async Task ResultWithValue_FailsWithTheSameErrors()
    {
        var handler = new CountingHandler<GetTestEntry, Result<string>>(Result.Ok("entry"));
        var behavior = new ValidationBehavior<GetTestEntry, Result<string>>([new GetTestEntryValidator()]);

        var result = await behavior.Handle(new GetTestEntry(Guid.Empty), handler.Next, Cancellation);

        handler.Calls.ShouldBe(0);
        AppErrors(result).ShouldHaveSingleItem().Parameters["field"].ShouldBe("entryId");
    }

    private static List<AppError> AppErrors(IResultBase result)
    {
        result.IsFailed.ShouldBeTrue();
        result.Errors.ShouldAllBe(error => error is AppError);
        return [.. result.Errors.Cast<AppError>()];
    }

    private static ValidationBehavior<AddTestEntry, Result> Behavior() => new([new AddTestEntryValidator()]);

    private static AddTestEntry Valid() => new("Groceries", 120.50m);

    private sealed class AddTestEntryValidator : AbstractValidator<AddTestEntry>
    {
        public AddTestEntryValidator()
        {
            RuleFor(command => command.Note).NotEmpty();
            RuleFor(command => command.Amount).GreaterThan(0m);
        }
    }

    private sealed class NoteLengthValidator : AbstractValidator<AddTestEntry>
    {
        public NoteLengthValidator() => RuleFor(command => command.Note).MaximumLength(200);
    }

    private sealed class SplitTestPaymentValidator : AbstractValidator<SplitTestPayment>
    {
        public SplitTestPaymentValidator() =>
            RuleForEach(command => command.Lines).ChildRules(line => line.RuleFor(item => item.Amount).GreaterThan(0m));
    }

    private sealed class GetTestEntryValidator : AbstractValidator<GetTestEntry>
    {
        public GetTestEntryValidator() => RuleFor(query => query.EntryId).NotEmpty();
    }
}

internal sealed record SplitTestPayment(IReadOnlyList<SplitLine> Lines) : ICommand<Result>;

internal sealed record SplitLine(decimal Amount);

internal sealed record GetTestEntry(Guid EntryId) : IQuery<Result<string>>;
