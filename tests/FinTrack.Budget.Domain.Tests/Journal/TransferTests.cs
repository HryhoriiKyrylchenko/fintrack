using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Domain.Tests.Journal;

/// <summary>
/// A transfer (ARCHITECTURE 4.2, plan 1a D6): one entry for both sides. The amounts are the truth;
/// the rate is derived from them and stored for display.
/// </summary>
public class TransferTests
{
    private static readonly OwnerId Owner = OwnerId.From(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly BucketId Cushion = BucketId.From(Guid.Parse("0190a1b2-c3d4-7e5f-8a6b-000000000001"));
    private static readonly BucketId Holiday = BucketId.From(Guid.Parse("0190a1b2-c3d4-7e5f-8a6b-000000000002"));
    private static readonly DateOnly Today = new(2026, 1, 15);

    [Fact]
    public void SameCurrency_WithoutFee_MovesTheAmount_AtRateOne()
    {
        var transfer = Create(Pln(100m), Pln(100m)).Value;

        transfer.FromBucketId.ShouldBe(Cushion);
        transfer.ToBucketId.ShouldBe(Holiday);
        transfer.FromAmount.ShouldBe(Pln(100m));
        transfer.ToAmount.ShouldBe(Pln(100m));
        transfer.Fee.ShouldBeNull();
        transfer.Rate.ShouldBe(1m);
    }

    [Fact]
    public void SameCurrency_WithFee_ArrivesWithoutTheFee()
    {
        var transfer = Create(Pln(100m), Pln(99.50m), Pln(0.50m)).Value;

        transfer.Fee.ShouldBe(Pln(0.50m));
        transfer.Rate.ShouldBe(1m);
    }

    [Theory]
    [InlineData(100, 99, null)]
    [InlineData(100, 100, 0.5)]
    [InlineData(100, 99.49, 0.5)]
    public void SameCurrency_WhereToIsNotFromMinusFee_IsInconsistent(double from, double to, double? fee) =>
        Create(Pln((decimal)from), Pln((decimal)to), fee is null ? null : Pln((decimal)fee.Value))
            .ShouldFailWith(BudgetErrorCodes.TransferAmountsInconsistent, ErrorKind.Validation);

    [Fact]
    public void CrossCurrency_DerivesTheRateFromTheAmounts_ExcludingTheFee()
    {
        // 1000.00 PLN leave, 10.00 PLN of it is the fee, 232.65 EUR arrive: 232.65 / 990 = 0.235.
        var transfer = Create(Pln(1000m), Eur(232.65m), Pln(10m)).Value;

        transfer.Rate.ShouldBe(0.235m);
    }

    [Fact]
    public void Rate_IsRoundedTo12DecimalPlaces()
    {
        var transfer = Create(Pln(300m), Eur(100m)).Value;

        transfer.Rate.ShouldBe(0.333333333333m);
        Transfer.RateDecimalPlaces.ShouldBe(12);
    }

    [Fact]
    public void SameBucket_IsRejected() =>
        Transfer.Create(NewId(), Owner, Cushion, Cushion, Pln(1m), Pln(1m), null, Today, Today, EntryInfo.None, FakeClock.DefaultStart)
            .ShouldFailWith(BudgetErrorCodes.TransferSameBucket, ErrorKind.Validation);

    [Theory]
    [InlineData(10, 10)]
    [InlineData(10, 12)]
    public void FeeNotBelowTheAmount_IsRejected(double from, double fee) =>
        Create(Pln((decimal)from), Eur(1m), Pln((decimal)fee))
            .ShouldFailWith(BudgetErrorCodes.TransferFeeExceedsAmount, ErrorKind.Validation);

    [Fact]
    public void FeeInAnotherCurrencyThanTheSource_IsACurrencyMismatch() =>
        Create(Pln(10m), Eur(2m), Eur(1m)).ShouldFailWith(BudgetErrorCodes.BucketCurrencyMismatch, ErrorKind.Validation);

    [Theory]
    [InlineData(0, 1, null)]
    [InlineData(1, 0, null)]
    [InlineData(10, 2, 0.0)]
    public void AmountsNotAboveZero_AreRejected(double from, double to, double? fee) =>
        Create(Pln((decimal)from), Eur((decimal)to), fee is null ? null : Pln((decimal)fee.Value))
            .ShouldFailWith(ErrorCodes.MoneyAmountNotPositive, ErrorKind.Validation);

    [Fact]
    public void DateAfterToday_IsRejected() =>
        Transfer.Create(NewId(), Owner, Cushion, Holiday, Pln(1m), Pln(1m), null, Today.AddDays(1), Today, EntryInfo.None, FakeClock.DefaultStart)
            .ShouldFailWith(BudgetErrorCodes.EntryDateInFuture, ErrorKind.BusinessRule);

    [Fact]
    public void HasSameContent_ComparesBothSidesAndTheFee()
    {
        var id = NewId();
        var original = Transfer.Create(id, Owner, Cushion, Holiday, Pln(100m), Eur(23m), Pln(1m), Today, Today, EntryInfo.None, FakeClock.DefaultStart).Value;

        original.HasSameContent(Transfer.Create(id, Owner, Cushion, Holiday, Pln(100m), Eur(23m), Pln(1m), Today, Today, EntryInfo.None, FakeClock.DefaultStart.AddHours(1)).Value).ShouldBeTrue();
        original.HasSameContent(Transfer.Create(id, Owner, Cushion, Holiday, Pln(100m), Eur(23m), null, Today, Today, EntryInfo.None, FakeClock.DefaultStart).Value).ShouldBeFalse();
        original.HasSameContent(Transfer.Create(id, Owner, Cushion, Holiday, Pln(100m), Eur(23.01m), Pln(1m), Today, Today, EntryInfo.None, FakeClock.DefaultStart).Value).ShouldBeFalse();
        original.HasSameContent(Transfer.Create(id, Owner, Holiday, Cushion, Pln(100m), Eur(23m), Pln(1m), Today, Today, EntryInfo.None, FakeClock.DefaultStart).Value).ShouldBeFalse();
    }

    private static FluentResults.Result<Transfer> Create(Money from, Money to, Money? fee = null) =>
        Transfer.Create(NewId(), Owner, Cushion, Holiday, from, to, fee, Today, Today, EntryInfo.None, FakeClock.DefaultStart);

    private static JournalEntryId NewId() => JournalEntryId.From(Guid.CreateVersion7());

    private static Money Pln(decimal amount) => Money.From(amount, Currency.Pln);

    private static Money Eur(decimal amount) => Money.From(amount, Currency.Eur);
}
