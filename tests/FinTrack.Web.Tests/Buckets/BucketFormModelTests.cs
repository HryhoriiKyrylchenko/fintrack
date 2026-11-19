using FinTrack.Budget.Contracts.V1.Buckets;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Web.Buckets;

namespace FinTrack.Web.Tests.Buckets;

/// <summary>
/// The create and rename form: the server's name rules checked before sending (rejected, never trimmed), the
/// profile only for an investment bucket, one client ID per form so a double submit creates one bucket.
/// </summary>
public class BucketFormModelTests
{
    private static readonly BucketDto Holiday = new(Guid.Parse("0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b"), "Holiday", Currency.Eur, BucketKind.Savings, null, BucketStatus.Active, BucketSystemRole.None);

    [Fact]
    public void NewForm_StartsInTheReportingCurrency_AsASavingsBucket_WithAVersion7Id()
    {
        var form = BucketFormModel.ForCreate(Currency.Usd);

        form.IsRename.ShouldBeFalse();
        form.Currency.ShouldBe(Currency.Usd);
        form.Kind.ShouldBe(BucketKind.Savings);
        form.Profile.ShouldBeNull();
        form.Name.ShouldBe(string.Empty);
        form.Id.Version.ShouldBe(7);
    }

    [Fact]
    public void TheId_StaysTheSame_ForEverySubmitOfOneForm()
    {
        var form = BucketFormModel.ForCreate(Currency.Pln);
        form.Name = "Cushion";

        form.ToCreateRequest().Id.ShouldBe(form.ToCreateRequest().Id);
        BucketFormModel.ForCreate(Currency.Pln).Id.ShouldNotBe(form.Id);
    }

    [Theory]
    [InlineData("", BucketFormModel.NameRequired)]
    [InlineData("   ", BucketFormModel.NameRequired)]
    [InlineData(" Cushion", BucketFormModel.NameSpaces)]
    [InlineData("Cushion ", BucketFormModel.NameSpaces)]
    [InlineData("Cush\tion", BucketFormModel.NameControlCharacters)]
    [InlineData("Cush\nion", BucketFormModel.NameControlCharacters)]
    public void InvalidName_HasItsMessage(string name, string expected)
    {
        var form = BucketFormModel.ForCreate(Currency.Pln);
        form.Name = name;

        form.NameError.ShouldBe(expected);
        form.CanSubmit.ShouldBeFalse();
    }

    [Fact]
    public void Name_OfSixtyCharacters_IsValid_OfSixtyOne_IsTooLong()
    {
        var form = BucketFormModel.ForCreate(Currency.Pln);

        form.Name = new string('a', 60);
        form.NameError.ShouldBeNull();

        form.Name = new string('a', 61);
        form.NameError.ShouldBe(BucketFormModel.NameTooLong);
    }

    [Fact]
    public void Name_IsSentExactlyAsTyped()
    {
        var form = BucketFormModel.ForCreate(Currency.Pln);
        form.Name = "Wakacje w Zakopanem";

        form.CanSubmit.ShouldBeTrue();
        form.ToCreateRequest().Name.ShouldBe("Wakacje w Zakopanem");
    }

    [Fact]
    public void InvestmentBucket_NeedsAProfile_ASavingsBucketSendsNone()
    {
        var form = BucketFormModel.ForCreate(Currency.Usd);
        form.Name = "Crypto";
        form.Kind = BucketKind.Investment;

        form.ProfileError.ShouldBe(BucketFormModel.ProfileRequired);
        form.CanSubmit.ShouldBeFalse();

        form.Profile = InvestmentProfile.Crypto;
        form.CanSubmit.ShouldBeTrue();
        form.ToCreateRequest().ShouldBe(new CreateBucketRequest(form.Id, "Crypto", Currency.Usd, BucketKind.Investment, InvestmentProfile.Crypto));

        // Switching back to savings keeps no profile in the request.
        form.Kind = BucketKind.Savings;
        form.ProfileError.ShouldBeNull();
        form.ToCreateRequest().Profile.ShouldBeNull();
    }

    [Fact]
    public void Rename_StartsWithTheCurrentName_AndNeedsARealChange()
    {
        var form = BucketFormModel.ForRename(Holiday);

        form.IsRename.ShouldBeTrue();
        form.Id.ShouldBe(Holiday.Id);
        form.Name.ShouldBe("Holiday");
        form.CanSubmit.ShouldBeFalse();

        form.Name = "holiday";
        form.CanSubmit.ShouldBeTrue("a change of case only is a rename");
        form.ToRenameRequest().ShouldBe(new RenameBucketRequest("holiday"));

        form.Name = "Holiday ";
        form.CanSubmit.ShouldBeFalse();
    }

    [Fact]
    public void Rename_OfASystemBucket_IsABug() =>
        Should.Throw<ArgumentException>(() => BucketFormModel.ForRename(Holiday with { Name = null, SystemRole = BucketSystemRole.Unallocated }));

    [Fact]
    public void Currencies_AreTheRegisteredFiatOnes() =>
        BucketFormModel.Currencies.ShouldBe([Currency.Pln, Currency.Eur, Currency.Usd]);
}
