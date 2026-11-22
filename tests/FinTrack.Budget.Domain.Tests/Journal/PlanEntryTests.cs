using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.Plans;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Shared.Kernel.Time;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Domain.Tests.Journal;

/// <summary>
/// <c>PlanCreated</c>, <c>PlanAmountChanged</c>, <c>PlanEndChanged</c> (ARCHITECTURE 4.2, plan 1b B7) and the optional
/// <c>planId</c> of <c>Deposit</c> and <c>Transfer</c> (B9): the values, every rule, and the content a repeat compares.
/// </summary>
public class PlanEntryTests
{
    private static readonly OwnerId Owner = OwnerId.From(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly BucketId Cushion = BucketId.From(Guid.Parse("01900000-0000-7000-8000-000000000001"));
    private static readonly BucketId Holiday = BucketId.From(Guid.Parse("01900000-0000-7000-8000-000000000002"));
    private static readonly JournalEntryId EntryId = JournalEntryId.From(Guid.Parse("01900000-0000-7000-8000-0000000000aa"));
    private static readonly PlanId Plan = PlanId.From(Guid.Parse("01900000-0000-7000-8000-0000000000bb"));
    private static readonly DateOnly Today = new(2026, 1, 15);
    private static readonly EntryInfo Info = new("Holiday fund", ["summer"], EntrySource.Manual);
    private static readonly YearMonth January = new(2026, 1);
    private static readonly YearMonth December = new(2026, 12);

    [Fact]
    public void PlanCreated_HoldsTheValues_AndItsIdIsThePlans()
    {
        var created = PlanCreated.Create(EntryId, Owner, Cushion, Pln(500.5m), January, December, Today.AddDays(-2), Today, Info, FakeClock.DefaultStart).Value;

        created.Id.ShouldBe(EntryId.Value);
        created.PlanId.ShouldBe(PlanId.From(EntryId.Value));
        created.OwnerId.ShouldBe(Owner);
        created.BucketId.ShouldBe(Cushion);
        created.Amount.ShouldBe(Pln(500.50m));
        created.ValidFrom.ShouldBe(January);
        created.ValidTo.ShouldBe(December);
        created.OccurredOn.ShouldBe(Today.AddDays(-2));
        created.RecordedAt.ShouldBe(FakeClock.DefaultStart);
        created.Note.ShouldBe("Holiday fund");
        created.Tags.ShouldBe(["summer"]);
        created.SchemaVersion.ShouldBe(PlanCreated.CurrentSchemaVersion);
        created.ShouldBeAssignableTo<IDefinitionEntry>();
    }

    [Fact]
    public void PlanCreated_MayHaveNoEnd_OrEndInItsFirstMonth()
    {
        PlanCreated.Create(EntryId, Owner, Cushion, Pln(1m), January, null, Today, Today, Info, FakeClock.DefaultStart).Value.ValidTo.ShouldBeNull();
        PlanCreated.Create(EntryId, Owner, Cushion, Pln(1m), January, January, Today, Today, Info, FakeClock.DefaultStart).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PlanCreated_WithoutAPositiveAmount_Fails(int amount) =>
        PlanCreated.Create(EntryId, Owner, Cushion, Pln(amount), January, null, Today, Today, Info, FakeClock.DefaultStart)
            .ShouldFailWith(ErrorCodes.MoneyAmountNotPositive, ErrorKind.Validation);

    [Fact]
    public void PlanCreated_EndingBeforeItStarts_Fails() =>
        PlanCreated.Create(EntryId, Owner, Cushion, Pln(1m), January, new YearMonth(2025, 12), Today, Today, Info, FakeClock.DefaultStart)
            .ShouldFailWith(BudgetErrorCodes.PlanMonthsInvalid, ErrorKind.Validation);

    [Fact]
    public void PlanCreated_InTheFuture_Fails_AndEveryBrokenRuleIsReported()
    {
        PlanCreated.Create(EntryId, Owner, Cushion, Pln(1m), January, null, Today.AddDays(1), Today, Info, FakeClock.DefaultStart)
            .ShouldFailWith(BudgetErrorCodes.EntryDateInFuture, ErrorKind.BusinessRule);

        var all = PlanCreated.Create(EntryId, Owner, Cushion, Pln(0m), January, new YearMonth(2025, 1), Today.AddDays(1), Today, Info, FakeClock.DefaultStart);
        all.Errors.Cast<AppError>().Select(error => error.Code).ShouldBe(
            [ErrorCodes.MoneyAmountNotPositive, BudgetErrorCodes.PlanMonthsInvalid, BudgetErrorCodes.EntryDateInFuture]);
    }

    [Fact]
    public void PlanAmountChanged_HoldsTheValues_AndChecksItsRules()
    {
        var changed = PlanAmountChanged.Create(EntryId, Owner, Plan, Pln(80m), December, Today, Today, Info, FakeClock.DefaultStart).Value;

        changed.PlanId.ShouldBe(Plan);
        changed.Amount.ShouldBe(Pln(80m));
        changed.From.ShouldBe(December);
        changed.OccurredOn.ShouldBe(Today);
        changed.SchemaVersion.ShouldBe(PlanAmountChanged.CurrentSchemaVersion);
        changed.ShouldBeAssignableTo<IDefinitionEntry>();

        PlanAmountChanged.Create(EntryId, Owner, Plan, Pln(0m), December, Today, Today, Info, FakeClock.DefaultStart)
            .ShouldFailWith(ErrorCodes.MoneyAmountNotPositive, ErrorKind.Validation);
        PlanAmountChanged.Create(EntryId, Owner, Plan, Pln(1m), December, Today.AddDays(1), Today, Info, FakeClock.DefaultStart)
            .ShouldFailWith(BudgetErrorCodes.EntryDateInFuture, ErrorKind.BusinessRule);
    }

    [Fact]
    public void PlanEndChanged_HoldsTheValues_MayRemoveTheEnd_AndChecksTheDate()
    {
        var ended = PlanEndChanged.Create(EntryId, Owner, Plan, December, Today, Today, Info, FakeClock.DefaultStart).Value;

        ended.PlanId.ShouldBe(Plan);
        ended.ValidTo.ShouldBe(December);
        ended.SchemaVersion.ShouldBe(PlanEndChanged.CurrentSchemaVersion);
        ended.ShouldBeAssignableTo<IDefinitionEntry>();
        PlanEndChanged.Create(EntryId, Owner, Plan, null, Today, Today, Info, FakeClock.DefaultStart).Value.ValidTo.ShouldBeNull();
        PlanEndChanged.Create(EntryId, Owner, Plan, December, Today.AddDays(1), Today, Info, FakeClock.DefaultStart)
            .ShouldFailWith(BudgetErrorCodes.EntryDateInFuture, ErrorKind.BusinessRule);
    }

    [Fact]
    public void ARepeat_ComparesEveryField()
    {
        var created = Created(Pln(500m), January, December);
        created.HasSameContent(Created(Pln(500m), January, December, FakeClock.DefaultStart.AddDays(1))).ShouldBeTrue();
        created.HasSameContent(Created(Pln(501m), January, December)).ShouldBeFalse();
        created.HasSameContent(Created(Pln(500m), new YearMonth(2026, 2), December)).ShouldBeFalse();
        created.HasSameContent(Created(Pln(500m), January, null)).ShouldBeFalse();
        created.HasSameContent(PlanCreated.Create(EntryId, Owner, Holiday, Pln(500m), January, December, Today, Today, Info, FakeClock.DefaultStart).Value).ShouldBeFalse();

        var changed = PlanAmountChanged.Create(EntryId, Owner, Plan, Pln(80m), December, Today, Today, Info, FakeClock.DefaultStart).Value;
        changed.HasSameContent(PlanAmountChanged.Create(EntryId, Owner, Plan, Pln(80m), December, Today, Today, Info, FakeClock.DefaultStart.AddDays(1)).Value).ShouldBeTrue();
        changed.HasSameContent(PlanAmountChanged.Create(EntryId, Owner, Plan, Pln(81m), December, Today, Today, Info, FakeClock.DefaultStart).Value).ShouldBeFalse();
        changed.HasSameContent(PlanAmountChanged.Create(EntryId, Owner, Plan, Pln(80m), January, Today, Today, Info, FakeClock.DefaultStart).Value).ShouldBeFalse();
        changed.HasSameContent(PlanAmountChanged.Create(EntryId, Owner, PlanId.From(Holiday.Value), Pln(80m), December, Today, Today, Info, FakeClock.DefaultStart).Value).ShouldBeFalse();

        var ended = PlanEndChanged.Create(EntryId, Owner, Plan, December, Today, Today, Info, FakeClock.DefaultStart).Value;
        ended.HasSameContent(PlanEndChanged.Create(EntryId, Owner, Plan, December, Today, Today, Info, FakeClock.DefaultStart.AddDays(1)).Value).ShouldBeTrue();
        ended.HasSameContent(PlanEndChanged.Create(EntryId, Owner, Plan, null, Today, Today, Info, FakeClock.DefaultStart).Value).ShouldBeFalse();
        ended.HasSameContent(PlanEndChanged.Create(EntryId, Owner, PlanId.From(Holiday.Value), December, Today, Today, Info, FakeClock.DefaultStart).Value).ShouldBeFalse();
    }

    [Fact]
    public void DepositsAndTransfers_HoldAnOptionalPlan_ThatARepeatCompares()
    {
        var deposit = Deposit.Create(EntryId, Owner, Cushion, Pln(10m), Today, Today, Info, FakeClock.DefaultStart, Plan).Value;
        var oneTime = Deposit.Create(EntryId, Owner, Cushion, Pln(10m), Today, Today, Info, FakeClock.DefaultStart).Value;

        deposit.PlanId.ShouldBe(Plan);
        oneTime.PlanId.ShouldBeNull();
        deposit.HasSameContent(oneTime).ShouldBeFalse();
        oneTime.HasSameContent(deposit).ShouldBeFalse();
        deposit.HasSameContent(Deposit.Create(EntryId, Owner, Cushion, Pln(10m), Today, Today, Info, FakeClock.DefaultStart.AddDays(1), Plan).Value).ShouldBeTrue();

        var transfer = Transfer.Create(EntryId, Owner, Cushion, Holiday, Pln(10m), Pln(10m), null, Today, Today, Info, FakeClock.DefaultStart, Plan).Value;
        var plain = Transfer.Create(EntryId, Owner, Cushion, Holiday, Pln(10m), Pln(10m), null, Today, Today, Info, FakeClock.DefaultStart).Value;

        transfer.PlanId.ShouldBe(Plan);
        plain.PlanId.ShouldBeNull();
        transfer.HasSameContent(plain).ShouldBeFalse();
        transfer.HasSameContent(Transfer.Create(EntryId, Owner, Cushion, Holiday, Pln(10m), Pln(10m), null, Today, Today, Info, FakeClock.DefaultStart.AddDays(1), Plan).Value).ShouldBeTrue();
    }

    private static PlanCreated Created(Money amount, YearMonth from, YearMonth? to, DateTimeOffset? recordedAt = null) =>
        PlanCreated.Create(EntryId, Owner, Cushion, amount, from, to, Today, Today, Info, recordedAt ?? FakeClock.DefaultStart).Value;

    private static Money Pln(decimal amount) => Money.From(amount, Currency.Pln);
}
