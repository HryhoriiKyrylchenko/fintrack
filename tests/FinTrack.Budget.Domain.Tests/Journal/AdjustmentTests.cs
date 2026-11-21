using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Domain.Tests.Journal;

/// <summary>
/// <c>Adjustment</c> (ARCHITECTURE 4.2, plan 1b B14): one bucket, a strictly positive amount and a direction,
/// with the date rule of every entry and a required note (the reason).
/// </summary>
public class AdjustmentTests
{
    private static readonly OwnerId Owner = OwnerId.From(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly BucketId Cushion = BucketId.From(Guid.Parse("0190a1b2-c3d4-7e5f-8a6b-000000000001"));
    private static readonly JournalEntryId EntryId = JournalEntryId.From(Guid.Parse("01900000-0000-7000-8000-0000000000aa"));
    private static readonly DateOnly Today = new(2026, 1, 15);
    private static readonly EntryInfo Reason = new("Counted the cash box", ["cash"], EntrySource.Manual);

    [Theory]
    [InlineData(AdjustmentDirection.Increase)]
    [InlineData(AdjustmentDirection.Decrease)]
    public void Create_HoldsTheValues(AdjustmentDirection direction)
    {
        var entry = Create(Money.From(80.25m, Currency.Pln), direction, Today.AddDays(-40)).Value;

        entry.Id.ShouldBe(EntryId.Value);
        entry.OwnerId.ShouldBe(Owner);
        entry.BucketId.ShouldBe(Cushion);
        entry.Amount.ShouldBe(Money.From(80.25m, Currency.Pln));
        entry.Direction.ShouldBe(direction);
        entry.OccurredOn.ShouldBe(Today.AddDays(-40));
        entry.RecordedAt.ShouldBe(FakeClock.DefaultStart);
        entry.Note.ShouldBe("Counted the cash box");
        entry.Tags.ShouldBe(["cash"]);
        entry.Source.ShouldBe(EntrySource.Manual);
        entry.SchemaVersion.ShouldBe(Adjustment.CurrentSchemaVersion);
    }

    [Fact]
    public void Today_IsAllowed()
    {
        Create(Money.From(1m, Currency.Pln), AdjustmentDirection.Decrease, Today).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void AmountNotAboveZero_AndAFutureDate_Fail()
    {
        var result = Create(Money.Zero(Currency.Pln), AdjustmentDirection.Increase, Today.AddDays(1));

        result.Errors.Cast<AppError>().Select(error => error.Code).ShouldBe([ErrorCodes.MoneyAmountNotPositive, BudgetErrorCodes.EntryDateInFuture]);
    }

    [Fact]
    public void ANegativeAmount_Fails()
    {
        Create(Money.From(-5m, Currency.Pln), AdjustmentDirection.Decrease, Today)
            .Errors.Cast<AppError>().Select(error => error.Code).ShouldBe([ErrorCodes.MoneyAmountNotPositive]);
    }

    [Fact]
    public void WithoutANote_IsABug()
    {
        // The validator requires the note (validation.failed, NotEmptyValidator); one that reaches the domain without it is a bug.
        Should.Throw<ArgumentException>(() => Adjustment.Create(
            EntryId, Owner, Cushion, Money.From(1m, Currency.Pln), AdjustmentDirection.Increase, Today, Today, EntryInfo.None, FakeClock.DefaultStart));
    }

    [Fact]
    public void AnUndefinedDirection_IsABug()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Create(Money.From(1m, Currency.Pln), (AdjustmentDirection)7, Today));
    }

    [Fact]
    public void TheDirection_IsPartOfTheContent()
    {
        var amount = Money.From(5m, Currency.Pln);
        var increase = Create(amount, AdjustmentDirection.Increase, Today).Value;

        increase.HasSameContent(Create(amount, AdjustmentDirection.Increase, Today).Value).ShouldBeTrue();
        increase.HasSameContent(Create(amount, AdjustmentDirection.Decrease, Today).Value).ShouldBeFalse();
        Create(amount, AdjustmentDirection.Decrease, Today).Value.HasSameContent(increase).ShouldBeFalse();
        increase.HasSameContent(Create(Money.From(6m, Currency.Pln), AdjustmentDirection.Increase, Today).Value).ShouldBeFalse();
    }

    [Fact]
    public void AnAdjustment_NeverHasTheContentOfADepositOrSpend()
    {
        var amount = Money.From(5m, Currency.Pln);
        var deposit = Deposit.Create(EntryId, Owner, Cushion, amount, Today, Today, Reason, FakeClock.DefaultStart).Value;
        var spend = Spend.Create(EntryId, Owner, Cushion, amount, Today, Today, Reason, FakeClock.DefaultStart).Value;
        var increase = Create(amount, AdjustmentDirection.Increase, Today).Value;
        var decrease = Create(amount, AdjustmentDirection.Decrease, Today).Value;

        increase.HasSameContent(deposit).ShouldBeFalse();
        deposit.HasSameContent(increase).ShouldBeFalse();
        decrease.HasSameContent(spend).ShouldBeFalse();
        spend.HasSameContent(decrease).ShouldBeFalse();
    }

    private static FluentResults.Result<Adjustment> Create(Money amount, AdjustmentDirection direction, DateOnly occurredOn) =>
        Adjustment.Create(EntryId, Owner, Cushion, amount, direction, occurredOn, Today, Reason, FakeClock.DefaultStart);
}
