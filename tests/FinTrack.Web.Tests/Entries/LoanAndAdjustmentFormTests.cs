using System.Globalization;
using System.Text.Json;
using FinTrack.Budget.Contracts.V1.Buckets;
using FinTrack.Budget.Contracts.V1.Journal;
using FinTrack.Budget.Contracts.V1.Loans;
using FinTrack.Shared.Kernel.Json;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Web.Entries;

namespace FinTrack.Web.Tests.Entries;

/// <summary>
/// Borrow, repay and adjust in the "Add entry" form (plan 1b step 7): the choices offer only what the server allows (B2),
/// a repayment is held to what its loan still owes (B4), an adjustment needs its reason (B14), and each becomes its request.
/// </summary>
public class LoanAndAdjustmentFormTests
{
    private static readonly DateOnly Today = new(2026, 1, 15);
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    private static readonly BucketDto Cushion = Bucket("Cushion", Currency.Pln);
    private static readonly BucketDto Car = Bucket("Car", Currency.Pln);
    private static readonly BucketDto Holiday = Bucket("Holiday", Currency.Eur);
    private static readonly BucketDto Stocks = Bucket("Stocks", Currency.Pln) with { Kind = BucketKind.Investment, Profile = InvestmentProfile.RealValueAssets };
    private static readonly BucketDto UnallocatedPln = Bucket(null, Currency.Pln) with { SystemRole = BucketSystemRole.Unallocated };

    private static readonly IReadOnlyList<BucketDto> Buckets = [Cushion, Car, Holiday, Stocks, UnallocatedPln];

    private static readonly LoanDto Open = Loan(Cushion, Car, 300m, 60m, LoanStatus.Open);
    private static readonly LoanDto Closed = Loan(Cushion, null, 50m, 50m, LoanStatus.Closed);

    [Fact]
    public void Lenders_AreTheOwnersSavingsBuckets_NeverUnallocatedOrAnInvestment()
    {
        var form = Form(EntryKind.Borrow);

        form.Lenders.Select(target => target.BucketId).ShouldBe([Cushion.Id, Car.Id, Holiday.Id]);
    }

    [Fact]
    public void Destinations_AreTheLendersCurrency_WithoutTheLender()
    {
        var form = Form(EntryKind.Borrow);
        form.LenderKey = Cushion.Id.ToString();

        form.Destinations.Select(target => target.Key).ShouldBe([Car.Id.ToString(), Stocks.Id.ToString(), "unallocated:PLN"]);
    }

    [Fact]
    public void Borrow_IntoABucket_AndToOutside_BecomeTheirRequests()
    {
        var form = Form(EntryKind.Borrow);
        form.LenderKey = Cushion.Id.ToString();
        form.DestinationKey = Car.Id.ToString();
        form.AmountText = "300";
        form.Note = "Repair";

        var request = form.ToDraft()!.ToRequest();

        request.Path.ShouldBe("v1/journal/borrows");
        request.Body.ShouldBe(new AddBorrowRequest(form.Id, Cushion.Id, Pln(300m), Today, Car.Id, null, "Repair", []), new BodyComparer());

        form.DestinationKey = EntryFormModel.OutsideKey;
        form.ToDraft()!.To.ShouldBeNull();
        ((AddBorrowRequest)form.ToDraft()!.ToRequest().Body).ShouldSatisfyAllConditions(
            body => body.ToBucketId.ShouldBeNull(),
            body => body.ToUnallocated.ShouldBeNull());

        form.DestinationKey = "unallocated:PLN";
        ((AddBorrowRequest)form.ToDraft()!.ToRequest().Body).ToUnallocated.ShouldBe(Currency.Pln);
    }

    [Fact]
    public void Borrow_NeedsALenderAndADestination_AndAnAmountInTheLendersCurrency()
    {
        var form = Form(EntryKind.Borrow);

        form.Validate().Keys.ShouldBe([EntryField.Lender]);

        form.LenderKey = Cushion.Id.ToString();
        form.AmountText = "1,005";
        var errors = form.Validate();
        errors[EntryField.Destination].Code.ShouldBe(EntryFormModel.TargetRequired);
        errors[EntryField.Amount].Code.ShouldBe("money.too_many_decimals");

        form.DestinationKey = Holiday.Id.ToString();
        form.Validate()[EntryField.Destination].Code.ShouldBe(EntryFormModel.TargetRequired);
    }

    [Fact]
    public void Loans_AreTheOpenOnes_AndSources_AreTheLoansCurrency_WithoutTheLender()
    {
        var form = Form(EntryKind.Repay);

        form.OpenLoans.ShouldBe([Open]);
        form.Sources.ShouldBeEmpty();

        form.LoanKey = Open.Id.ToString();
        form.Sources.Select(target => target.Key).ShouldBe([Car.Id.ToString(), Stocks.Id.ToString(), "unallocated:PLN"]);
    }

    [Fact]
    public void Repay_FromABucket_AndFromOutside_BecomeTheirRequests()
    {
        var form = Repay("240");
        form.SourceKey = Car.Id.ToString();

        var draft = form.ToDraft()!;
        draft.Loan.ShouldBe(Open);
        draft.To!.BucketId.ShouldBe(Cushion.Id);
        draft.ToRequest().Path.ShouldBe("v1/journal/repayments");
        draft.ToRequest().Body.ShouldBe(new AddRepaymentRequest(form.Id, Open.Id, Pln(240m), Today, Car.Id, null, null, []), new BodyComparer());

        form.SourceKey = EntryFormModel.OutsideKey;
        form.ToDraft()!.ToRequest().Body.ShouldBe(new AddRepaymentRequest(form.Id, Open.Id, Pln(240m), Today, null, null, null, []), new BodyComparer());
    }

    [Fact]
    public void Repay_AboveWhatTheLoanStillOwes_IsRefusedWithTheServersCode()
    {
        var form = Repay("240,01");
        form.SourceKey = EntryFormModel.OutsideKey;

        form.ToDraft().ShouldBeNull();
        form.Validate()[EntryField.Amount].Code.ShouldBe("loan.repayment_exceeds_outstanding");
    }

    [Fact]
    public void Repay_NeedsALoan_AndASource()
    {
        var form = Form(EntryKind.Repay);
        form.Validate()[EntryField.Loan].Code.ShouldBe(EntryFormModel.LoanRequired);

        form.LoanKey = Closed.Id.ToString();
        form.Validate()[EntryField.Loan].Code.ShouldBe(EntryFormModel.LoanRequired);

        form.LoanKey = Open.Id.ToString();
        form.AmountText = "1";
        form.Validate()[EntryField.Source].Code.ShouldBe(EntryFormModel.TargetRequired);
        form.SourceKey = Cushion.Id.ToString();
        form.Validate()[EntryField.Source].Code.ShouldBe(EntryFormModel.TargetRequired);
    }

    [Fact]
    public void ReplacingTheLoans_DropsAChosenLoanThatIsNoLongerOpen()
    {
        var form = Repay("1");

        form.ReplaceLoans([Open with { Status = LoanStatus.Closed }, Closed]);

        form.LoanKey.ShouldBeNull();
        form.OpenLoans.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(AdjustmentDirection.Increase)]
    [InlineData(AdjustmentDirection.Decrease)]
    public void Adjust_AnyBucket_BecomesItsRequest_WithTheDirectionAndTheReason(AdjustmentDirection direction)
    {
        var form = Form(EntryKind.Adjust);
        form.Targets.Select(target => target.BucketId).ShouldContain(Stocks.Id);
        form.TargetKey = "unallocated:EUR";
        form.Direction = direction;
        form.AmountText = "3,5";
        form.Note = "Counted the cash box";

        var request = form.ToDraft()!.ToRequest();

        request.Path.ShouldBe("v1/journal/adjustments");
        request.Body.ShouldBe(new AddAdjustmentRequest(form.Id, Money.From(3.50m, Currency.Eur), direction, Today, "Counted the cash box", null, Currency.Eur, []), new BodyComparer());
    }

    [Fact]
    public void Adjust_WithoutAReason_IsRefused_ButOtherKindsNeedNone()
    {
        var form = Form(EntryKind.Adjust);
        form.TargetKey = Cushion.Id.ToString();
        form.AmountText = "1";

        form.Validate()[EntryField.Note].Code.ShouldBe(EntryFormModel.ReasonRequired);

        form.Kind = EntryKind.Deposit;
        form.Validate().ShouldBeEmpty();
        form.ToDraft()!.Direction.ShouldBeNull();
    }

    private static EntryFormModel Form(EntryKind kind) => EntryFormModel.Create(kind, Buckets, Today, Polish, [Open, Closed]);

    private static EntryFormModel Repay(string amount)
    {
        var form = Form(EntryKind.Repay);
        form.LoanKey = Open.Id.ToString();
        form.AmountText = amount;
        return form;
    }

    private static LoanDto Loan(BucketDto lender, BucketDto? to, decimal principal, decimal repaid, LoanStatus status) =>
        new(Guid.CreateVersion7(), lender.Id, to?.Id, Pln(principal), Today.AddDays(-10), Pln(repaid), Pln(principal - repaid), status,
            status == LoanStatus.Closed ? Today.AddDays(-1) : null, null);

    private static BucketDto Bucket(string? name, Currency currency) =>
        new(Guid.CreateVersion7(), name, currency, BucketKind.Savings, null, BucketStatus.Active, BucketSystemRole.None);

    private static Money Pln(decimal amount) => Money.From(amount, Currency.Pln);

    // Request records compare their tags list by reference; compare them as their JSON instead.
    private sealed class BodyComparer : IEqualityComparer<object>
    {
        public new bool Equals(object? x, object? y) =>
            JsonSerializer.Serialize(x, FinTrackJson.Options) == JsonSerializer.Serialize(y, FinTrackJson.Options);

        public int GetHashCode(object obj) => 0;
    }
}
