using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Domain.Tests.Journal;

/// <summary>
/// <c>OpeningBalance</c> and <c>Spend</c> (ARCHITECTURE 4.2): one bucket and a strictly positive
/// amount, like a deposit, with the same date rule (D12, D13).
/// </summary>
public class OpeningBalanceAndSpendTests
{
    private static readonly OwnerId Owner = OwnerId.From(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly BucketId Cushion = BucketId.From(Guid.Parse("0190a1b2-c3d4-7e5f-8a6b-000000000001"));
    private static readonly DateOnly Today = new(2026, 1, 15);

    public static TheoryData<string> Types => ["OpeningBalance", "Spend"];

    [Theory]
    [MemberData(nameof(Types))]
    public void Create_HoldsTheValues(string type)
    {
        var id = JournalEntryId.From(Guid.CreateVersion7());

        var entry = Create(type, id, Money.From(80.25m, Currency.Pln), Today.AddDays(-400)).Value;

        entry.Id.ShouldBe(id.Value);
        entry.BucketId.ShouldBe(Cushion);
        entry.Amount.ShouldBe(Money.From(80.25m, Currency.Pln));
        entry.OccurredOn.ShouldBe(Today.AddDays(-400));
        entry.RecordedAt.ShouldBe(FakeClock.DefaultStart);
        entry.Source.ShouldBe(EntrySource.Manual);
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void AmountNotAboveZero_AndAFutureDate_Fail(string type)
    {
        var result = Create(type, JournalEntryId.From(Guid.CreateVersion7()), Money.Zero(Currency.Pln), Today.AddDays(1));

        result.Errors.Cast<AppError>().Select(error => error.Code).ShouldBe([ErrorCodes.MoneyAmountNotPositive, BudgetErrorCodes.EntryDateInFuture]);
    }

    [Fact]
    public void EntriesOfDifferentTypes_NeverHaveTheSameContent()
    {
        var id = JournalEntryId.From(Guid.CreateVersion7());
        var amount = Money.From(5m, Currency.Pln);

        var deposit = Deposit.Create(id, Owner, Cushion, amount, Today, Today, EntryInfo.None, FakeClock.DefaultStart).Value;
        var spend = Create("Spend", id, amount, Today).Value;
        var opening = Create("OpeningBalance", id, amount, Today).Value;

        deposit.HasSameContent(spend).ShouldBeFalse();
        spend.HasSameContent(opening).ShouldBeFalse();
        spend.HasSameContent(Create("Spend", id, amount, Today).Value).ShouldBeTrue();
    }

    private static FluentResults.Result<SingleBucketEntry> Create(string type, JournalEntryId id, Money amount, DateOnly occurredOn) => type switch
    {
        "OpeningBalance" => OpeningBalance.Create(id, Owner, Cushion, amount, occurredOn, Today, EntryInfo.None, FakeClock.DefaultStart).Map(entry => (SingleBucketEntry)entry),
        "Spend" => Spend.Create(id, Owner, Cushion, amount, occurredOn, Today, EntryInfo.None, FakeClock.DefaultStart).Map(entry => (SingleBucketEntry)entry),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
