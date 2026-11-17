using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Domain.Tests.Journal;

/// <summary>
/// A deposit (ARCHITECTURE 4.2): new money into one bucket. Amounts are strictly positive (D13), the
/// date is never after today in the owner's time zone (D12), and the descriptive fields have fixed rules.
/// </summary>
public class DepositTests
{
    private static readonly OwnerId Owner = OwnerId.From(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly BucketId Cushion = BucketId.From(Guid.Parse("0190a1b2-c3d4-7e5f-8a6b-000000000001"));
    private static readonly DateOnly Today = new(2026, 1, 15);

    [Fact]
    public void Create_HoldsTheValues()
    {
        var id = JournalEntryId.From(Guid.CreateVersion7());

        var deposit = Deposit.Create(id, Owner, Cushion, Money.From(250.00m, Currency.Pln), Today, Today,
            new EntryInfo("Salary", ["work", "monthly"], EntrySource.Manual), FakeClock.DefaultStart).Value;

        deposit.Id.ShouldBe(id.Value);
        deposit.EntryId.ShouldBe(id);
        deposit.OwnerId.ShouldBe(Owner);
        deposit.SchemaVersion.ShouldBe(Deposit.CurrentSchemaVersion);
        deposit.BucketId.ShouldBe(Cushion);
        deposit.Amount.ShouldBe(Money.From(250.00m, Currency.Pln));
        deposit.OccurredOn.ShouldBe(Today);
        deposit.RecordedAt.ShouldBe(FakeClock.DefaultStart);
        deposit.Note.ShouldBe("Salary");
        deposit.Tags.ShouldBe(["work", "monthly"]);
        deposit.Source.ShouldBe(EntrySource.Manual);
        deposit.GroupId.ShouldBeNull();
        deposit.ExternalId.ShouldBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.01)]
    public void Create_WithAnAmountNotAboveZero_Fails(double amount) =>
        Create(amount: Money.From((decimal)amount, Currency.Pln)).ShouldFailWith(ErrorCodes.MoneyAmountNotPositive, ErrorKind.Validation);

    [Fact]
    public void Create_DatedAfterToday_Fails() =>
        Create(occurredOn: Today.AddDays(1)).ShouldFailWith(BudgetErrorCodes.EntryDateInFuture, ErrorKind.BusinessRule);

    [Fact]
    public void Create_DatedToday_OrEarlier_IsAllowed()
    {
        Create(occurredOn: Today).IsSuccess.ShouldBeTrue();
        Create(occurredOn: new DateOnly(2019, 3, 1)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Create_ReportsBothFailures()
    {
        var result = Create(amount: Money.Zero(Currency.Pln), occurredOn: Today.AddDays(3));

        result.Errors.Cast<AppError>().Select(error => error.Code).ShouldBe([ErrorCodes.MoneyAmountNotPositive, BudgetErrorCodes.EntryDateInFuture]);
    }

    [Theory]
    [InlineData("Salary", "work,monthly", "Salary", "work,monthly", true)]
    [InlineData("Salary", "work", "Bonus", "work", false)]
    [InlineData("Salary", "work", "salary", "work", false)]
    [InlineData("Salary", "work", "Salary", "Work", false)]
    [InlineData("Salary", "work,monthly", "Salary", "monthly,work", false)]
    public void HasSameContent_ComparesEverythingButRecordedAt_Exactly(string note, string tags, string otherNote, string otherTags, bool expected)
    {
        var id = JournalEntryId.From(Guid.CreateVersion7());
        var first = Deposit.Create(id, Owner, Cushion, Money.From(10m, Currency.Pln), Today, Today, new EntryInfo(note, tags.Split(','), EntrySource.Manual), FakeClock.DefaultStart).Value;
        var second = Deposit.Create(id, Owner, Cushion, Money.From(10m, Currency.Pln), Today, Today, new EntryInfo(otherNote, otherTags.Split(','), EntrySource.Manual), FakeClock.DefaultStart.AddHours(1)).Value;

        first.HasSameContent(second).ShouldBe(expected);
    }

    [Fact]
    public void HasSameContent_SeesAmountDateAndBucket()
    {
        var id = JournalEntryId.From(Guid.CreateVersion7());
        var original = Create(id: id).Value;

        original.HasSameContent(Create(id: id, amount: Money.From(10.01m, Currency.Pln)).Value).ShouldBeFalse();
        original.HasSameContent(Create(id: id, occurredOn: Today.AddDays(-1)).Value).ShouldBeFalse();
        original.HasSameContent(Create(id: id, bucket: BucketId.From(Guid.CreateVersion7())).Value).ShouldBeFalse();
        original.HasSameContent(Create(id: id, amount: Money.From(10.00m, Currency.Pln)).Value).ShouldBeTrue();
    }

    private static FluentResults.Result<Deposit> Create(
        JournalEntryId? id = null,
        BucketId? bucket = null,
        Money? amount = null,
        DateOnly? occurredOn = null) =>
        Deposit.Create(
            id ?? JournalEntryId.From(Guid.CreateVersion7()),
            Owner,
            bucket ?? Cushion,
            amount ?? Money.From(10m, Currency.Pln),
            occurredOn ?? Today,
            Today,
            EntryInfo.None,
            FakeClock.DefaultStart);
}
