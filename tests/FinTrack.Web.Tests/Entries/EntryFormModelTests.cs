using System.Globalization;
using System.Text.Json;
using FinTrack.Budget.Contracts.V1.Buckets;
using FinTrack.Budget.Contracts.V1.Journal;
using FinTrack.Shared.Kernel.Json;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Web.Entries;
using FinTrack.Web.Formatting;

namespace FinTrack.Web.Tests.Entries;

/// <summary>
/// The "Add entry" form (plan 1a step 16): targets, the server's rules checked before sending with the server's
/// own codes where they exist, a transfer by received amount or by rate, and one ID per form so a double submit
/// or a retry writes once.
/// </summary>
public class EntryFormModelTests
{
    private static readonly DateOnly Today = new(2026, 1, 15);
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    private static readonly BucketDto Cushion = Bucket("Cushion", Currency.Pln);
    private static readonly BucketDto Holiday = Bucket("Holiday", Currency.Eur);
    private static readonly BucketDto Crypto = Bucket("Crypto", Currency.Usd) with { Kind = BucketKind.Investment, Profile = InvestmentProfile.Crypto };
    private static readonly BucketDto UnallocatedPln = Bucket(null, Currency.Pln) with { SystemRole = BucketSystemRole.Unallocated };

    private static readonly IReadOnlyList<BucketDto> Buckets = [Cushion, UnallocatedPln, Holiday, Crypto];

    [Fact]
    public void Targets_AreTheOwnersBuckets_ThenUnallocatedOfEveryCurrency()
    {
        var form = Form(EntryKind.Deposit);

        form.Targets.Select(target => target.Key).ShouldBe(
        [
            Cushion.Id.ToString(), Holiday.Id.ToString(), Crypto.Id.ToString(),
            "unallocated:PLN", "unallocated:EUR", "unallocated:USD",
        ]);
        form.Targets.Single(target => target.Key == "unallocated:PLN").IsUnallocated.ShouldBeTrue();
    }

    [Fact]
    public void Spend_CannotComeFromAnInvestmentBucket()
    {
        var form = Form(EntryKind.Spend);

        form.Targets.Select(target => target.BucketId).ShouldNotContain(Crypto.Id);
        form.TransferTargets.Select(target => target.BucketId).ShouldContain(Crypto.Id);
    }

    [Fact]
    public void NewForm_IsDatedToday_WithAVersion7Id_ThatStaysForEveryDraft()
    {
        var form = Deposit(Cushion, "12,30");

        form.OccurredOn.ShouldBe(Today);
        form.Id.Version.ShouldBe(7);
        form.ToDraft()!.Id.ShouldBe(form.Id);
        form.ToDraft()!.Id.ShouldBe(form.Id);
    }

    [Fact]
    public void Deposit_ToABucket_BecomesItsRequest()
    {
        var form = Deposit(Cushion, "1 234,5");
        form.Note = "Salary";
        form.AddTag("work").ShouldBeNull();

        var request = form.ToDraft()!.ToRequest();

        request.Path.ShouldBe("v1/journal/deposits");
        request.Body.ShouldBe(new AddDepositRequest(form.Id, Pln(1234.50m), Today, Cushion.Id, null, "Salary", ["work"]), new BodyComparer());
    }

    [Fact]
    public void Deposit_ToUnallocated_NamesItsCurrency()
    {
        var form = Form(EntryKind.Deposit);
        form.TargetKey = "unallocated:EUR";
        form.AmountText = "5";

        var body = (AddDepositRequest)form.ToDraft()!.ToRequest().Body;

        body.BucketId.ShouldBeNull();
        body.Unallocated.ShouldBe(Currency.Eur);
        body.Amount.ShouldBe(Money.From(5.00m, Currency.Eur));
    }

    [Theory]
    [InlineData(EntryKind.OpeningBalance, "v1/journal/opening-balances", typeof(AddOpeningBalanceRequest))]
    [InlineData(EntryKind.Deposit, "v1/journal/deposits", typeof(AddDepositRequest))]
    [InlineData(EntryKind.Spend, "v1/journal/spends", typeof(AddSpendRequest))]
    public void EachSingleBucketKind_HasItsRoute(EntryKind kind, string path, Type body)
    {
        var form = Form(kind);
        form.TargetKey = Cushion.Id.ToString();
        form.AmountText = "1";

        var request = form.ToDraft()!.ToRequest();

        request.Path.ShouldBe(path);
        request.Body.ShouldBeOfType(body);
    }

    [Fact]
    public void WithoutATarget_TheTargetIsRequired()
    {
        var form = Form(EntryKind.Deposit);
        form.AmountText = "1";

        form.Validate()[EntryField.Target].Code.ShouldBe(EntryFormModel.TargetRequired);
        form.ToDraft().ShouldBeNull();
    }

    [Theory]
    [InlineData("", "input.amount_required")]
    [InlineData("12.50", "input.amount_invalid")]
    [InlineData("1,234", "money.too_many_decimals")]
    [InlineData("0", "money.amount_not_positive")]
    [InlineData("-5", "money.amount_not_positive")]
    public void Amount_IsReadInTheTargetsCurrency_AndMustBeAboveZero(string text, string code) =>
        Deposit(Cushion, text).Validate()[EntryField.Amount].Code.ShouldBe(code);

    [Fact]
    public void AmountWithMoreDecimalsThanTheCurrency_NamesTheCurrency()
    {
        var error = Deposit(Cushion, "1,234").Validate()[EntryField.Amount];

        error.Parameters["currency"].ShouldBe("PLN");
        error.Parameters["maxDecimalPlaces"].ShouldBe("2");
    }

    [Fact]
    public void DateAfterToday_IsTheServersDateInFuture()
    {
        var form = Deposit(Cushion, "1");
        form.OccurredOn = Today.AddDays(1);

        form.Validate()[EntryField.Date].Code.ShouldBe("entry.date_in_future");

        form.OccurredOn = Today.AddDays(-400);
        form.Validate().ShouldBeEmpty("backdating is allowed");
    }

    [Theory]
    [InlineData(" Salary")]
    [InlineData("Salary ")]
    [InlineData("Sal\tary")]
    public void Note_FollowsTheServersRules(string note)
    {
        var form = Deposit(Cushion, "1");
        form.Note = note;

        form.Validate()[EntryField.Note].Code.ShouldBe(EntryFormModel.NoteInvalid);
    }

    [Fact]
    public void Note_MayHaveLineBreaks_AndAnEmptyNoteIsNone()
    {
        var form = Deposit(Cushion, "1");
        form.Note = "First line\nsecond line";
        form.Validate().ShouldBeEmpty();

        form.Note = string.Empty;
        ((AddDepositRequest)form.ToDraft()!.ToRequest().Body).Note.ShouldBeNull();

        form.Note = new string('a', 501);
        form.Validate()[EntryField.Note].Code.ShouldBe(EntryFormModel.NoteInvalid);
    }

    [Fact]
    public void Tags_AreDistinctWithoutCase_AtMostTen_AndNeverTrimmed()
    {
        var form = Deposit(Cushion, "1");

        form.AddTag("Work").ShouldBeNull();
        form.AddTag("work")!.Code.ShouldBe(EntryFormModel.TagDuplicate);
        form.AddTag(" home")!.Code.ShouldBe(EntryFormModel.TagInvalid);
        form.AddTag(new string('a', 31))!.Code.ShouldBe(EntryFormModel.TagInvalid);
        form.AddTag(string.Empty)!.Code.ShouldBe(EntryFormModel.TagInvalid);

        for (var index = 1; index < 10; index++)
        {
            form.AddTag($"tag{index}").ShouldBeNull();
        }

        form.AddTag("eleventh")!.Code.ShouldBe(EntryFormModel.TagsTooMany);
        form.Tags.Count.ShouldBe(10);

        form.RemoveTag("Work");
        form.Tags.ShouldNotContain("Work");
    }

    [Fact]
    public void SameCurrencyTransfer_ReceivesWhatWasSentLessTheFee()
    {
        var form = Transfer(Cushion, UnallocatedTarget(Currency.Pln), "100,00");
        form.FeeText = "1,50";

        form.IsSameCurrencyTransfer.ShouldBeTrue();
        var draft = form.ToDraft()!;

        draft.ToAmount.ShouldBe(Pln(98.50m));
        draft.Rate.ShouldBe(1m);
        draft.ToRequest().Body.ShouldBe(
            new AddTransferRequest(form.Id, Pln(100.00m), Pln(98.50m), Today, Cushion.Id, null, null, Currency.Pln, Pln(1.50m), null, []),
            new BodyComparer());
        draft.ToRequest().Path.ShouldBe("v1/journal/transfers");
    }

    [Fact]
    public void CrossCurrencyTransfer_ByReceivedAmount_ShowsTheDerivedRate()
    {
        var form = Transfer(Cushion, Holiday.Id.ToString(), "431,00");
        form.FeeText = "1,00";
        form.TransferInput = TransferInput.ReceivedAmount;
        form.ReceivedText = "100,00";

        var draft = form.ToDraft()!;

        draft.ToAmount.ShouldBe(Money.From(100.00m, Currency.Eur));
        draft.Rate.ShouldBe(0.232558139535m, "100 ÷ (431 − 1), banker's rounding to 12 places as the server stores it");
    }

    [Fact]
    public void CrossCurrencyTransfer_ByRate_ComputesTheReceivedAmount_RoundedOnce()
    {
        var form = Transfer(Cushion, Holiday.Id.ToString(), "100,00");
        form.TransferInput = TransferInput.Rate;
        form.RateText = "0,23255";

        var draft = form.ToDraft()!;

        // 100.00 × 0.23255 = 23.255, half-way between 23.25 and 23.26: the even neighbour is 23.26.
        draft.ToAmount.ShouldBe(Money.From(23.26m, Currency.Eur));
        ((AddTransferRequest)draft.ToRequest().Body).ToAmount.ShouldBe(Money.From(23.26m, Currency.Eur));
    }

    [Fact]
    public void CrossCurrencyTransfer_ByRate_UsesBankersRounding()
    {
        var form = Transfer(Cushion, Holiday.Id.ToString(), "1,00");
        form.TransferInput = TransferInput.Rate;
        form.RateText = "0,125";

        // 1.00 × 0.125 = 0.125, half-way between 0.12 and 0.13: the even neighbour is 0.12.
        form.ToDraft()!.ToAmount.ShouldBe(Money.From(0.12m, Currency.Eur));
    }

    [Fact]
    public void TransferRules_AreTheServersCodes()
    {
        var form = Transfer(Cushion, Cushion.Id.ToString(), "10");
        form.Validate()[EntryField.To].Code.ShouldBe("transfer.same_bucket");

        form.ToKey = "unallocated:PLN";
        form.FeeText = "10";
        form.Validate()[EntryField.Fee].Code.ShouldBe("transfer.fee_exceeds_amount");

        form.FeeText = "0";
        form.Validate()[EntryField.Fee].Code.ShouldBe("money.amount_not_positive");

        form.FromKey = null;
        form.Validate()[EntryField.From].Code.ShouldBe(EntryFormModel.TargetRequired);
    }

    [Fact]
    public void CrossCurrencyTransfer_NeedsWhatWasReceived_OrARate()
    {
        var form = Transfer(Cushion, Holiday.Id.ToString(), "10");

        form.TransferInput = TransferInput.ReceivedAmount;
        form.Validate()[EntryField.Received].Code.ShouldBe("input.amount_required");

        form.TransferInput = TransferInput.Rate;
        form.Validate()[EntryField.Rate].Code.ShouldBe(RateInput.Required);

        form.RateText = "0,0001";
        form.Validate()[EntryField.Rate].Code.ShouldBe("money.amount_not_positive", "10 PLN at 0.0001 is 0.00 EUR, nothing would arrive");
    }

    [Fact]
    public void StartAnother_KeepsKindDateAndTarget_WithANewId()
    {
        var form = Deposit(Cushion, "1");
        form.OccurredOn = Today.AddDays(-3);
        form.Note = "Gift";
        form.AddTag("family");
        var first = form.Id;

        form.StartAnother();

        form.Id.ShouldNotBe(first);
        form.Id.Version.ShouldBe(7);
        form.Kind.ShouldBe(EntryKind.Deposit);
        form.OccurredOn.ShouldBe(Today.AddDays(-3));
        form.TargetKey.ShouldBe(Cushion.Id.ToString());
        form.AmountText.ShouldBeNull();
        form.Note.ShouldBeNull();
        form.Tags.ShouldBeEmpty();
    }

    [Fact]
    public void Body_IsWrittenInTheJsonContract()
    {
        var json = JsonSerializer.Serialize(Deposit(Cushion, "12,3").ToDraft()!.ToRequest().Body, FinTrackJson.Options);

        json.ShouldContain("\"amount\":{\"amount\":\"12.30\",\"currency\":\"PLN\"}");
        json.ShouldContain("\"occurredOn\":\"2026-01-15\"");
    }

    private static EntryFormModel Form(EntryKind kind) => EntryFormModel.Create(kind, Buckets, Today, Polish);

    private static EntryFormModel Deposit(BucketDto target, string amount)
    {
        var form = Form(EntryKind.Deposit);
        form.TargetKey = target.Id.ToString();
        form.AmountText = amount;
        return form;
    }

    private static EntryFormModel Transfer(BucketDto from, string toKey, string amount)
    {
        var form = Form(EntryKind.Transfer);
        form.FromKey = from.Id.ToString();
        form.ToKey = toKey;
        form.AmountText = amount;
        return form;
    }

    private static string UnallocatedTarget(Currency currency) => $"unallocated:{currency.Code}";

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
