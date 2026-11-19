using FinTrack.Budget.Domain.Buckets;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Domain.Tests.Buckets;

/// <summary>
/// Buckets (ARCHITECTURE 4.1, plan 1a D5, D11, D15): kinds and profiles, unique names among the
/// owner's active buckets without regard to case, and the protected system bucket <c>Unallocated</c>.
/// </summary>
public class BucketTests
{
    private static readonly OwnerId Owner = OwnerId.From(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));

    [Fact]
    public void Create_Savings_HasNoProfile_AndIsActive()
    {
        var bucket = Create("Cushion").Value;

        bucket.Name.ShouldBe("Cushion");
        bucket.Currency.ShouldBe(Currency.Pln);
        bucket.Kind.ShouldBe(BucketKind.Savings);
        bucket.Profile.ShouldBeNull();
        bucket.Status.ShouldBe(BucketStatus.Active);
        bucket.SystemRole.ShouldBe(BucketSystemRole.None);
        bucket.Version.ShouldBe(0);
        bucket.SchemaVersion.ShouldBe(Bucket.CurrentSchemaVersion);
    }

    [Fact]
    public void Create_Investment_HasItsProfile()
    {
        var bucket = Bucket.Create(NewId(), Owner, "Crypto", Currency.Usd, BucketKind.Investment, InvestmentProfile.Crypto, []).Value;

        bucket.Kind.ShouldBe(BucketKind.Investment);
        bucket.Profile.ShouldBe(InvestmentProfile.Crypto);
    }

    [Theory]
    [InlineData("cushion")]
    [InlineData("CUSHION")]
    [InlineData("Cushion")]
    public void Create_WithTheNameOfAnActiveBucket_InAnyCase_IsNameTaken(string name)
    {
        var existing = Create("Cushion").Value;

        Create(name, [existing]).ShouldFailWith(BudgetErrorCodes.BucketNameTaken, ErrorKind.BusinessRule);
    }

    [Fact]
    public void NameComparison_FoldsPolishLettersAndUnicodeForms()
    {
        var existing = Create("Łódź café").Value;

        // Lower-case Polish letters, and "é" written as "e" plus a combining accent.
        Create("łódź café", [existing]).ShouldFailWith(BudgetErrorCodes.BucketNameTaken, ErrorKind.BusinessRule);
    }

    [Fact]
    public void Create_WithTheNameOfAnArchivedBucket_IsAllowed()
    {
        var archived = StoredBuckets.Restore(NewId(), Owner, "Cushion", Currency.Pln, BucketKind.Savings, null, BucketStatus.Archived, BucketSystemRole.None);

        Create("Cushion", [archived]).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Create_WithTheSameNameInAnotherCurrency_IsStillTaken() =>
        Create("Cushion", [Bucket.Create(NewId(), Owner, "Cushion", Currency.Eur, BucketKind.Savings, null, []).Value])
            .ShouldFailWith(BudgetErrorCodes.BucketNameTaken, ErrorKind.BusinessRule);

    [Theory]
    [InlineData("")]
    [InlineData(" Cushion")]
    [InlineData("Cushion ")]
    [InlineData("Cush\tion")]
    [InlineData("Cush\nion")]
    public void Create_WithAnInvalidName_IsABug(string name) =>
        Should.Throw<ArgumentException>(() => Create(name));

    [Fact]
    public void Names_HaveAtMost60Characters()
    {
        Bucket.IsValidName(new string('a', Bucket.MaxNameLength)).ShouldBeTrue();
        Bucket.IsValidName(new string('a', Bucket.MaxNameLength + 1)).ShouldBeFalse();
        Bucket.MaxNameLength.ShouldBe(60);
    }

    [Fact]
    public void Create_SavingsWithAProfile_OrInvestmentWithout_IsABug()
    {
        Should.Throw<ArgumentException>(() => Bucket.Create(NewId(), Owner, "A", Currency.Pln, BucketKind.Savings, InvestmentProfile.Crypto, []));
        Should.Throw<ArgumentException>(() => Bucket.Create(NewId(), Owner, "B", Currency.Pln, BucketKind.Investment, null, []));
    }

    [Fact]
    public void Unallocated_IsASavingsSystemBucketWithoutName()
    {
        var bucket = Bucket.Unallocated(NewId(), Owner, Currency.Eur);

        bucket.Name.ShouldBeNull();
        bucket.SystemRole.ShouldBe(BucketSystemRole.Unallocated);
        bucket.Kind.ShouldBe(BucketKind.Savings);
        bucket.Profile.ShouldBeNull();
        bucket.Currency.ShouldBe(Currency.Eur);
        bucket.Status.ShouldBe(BucketStatus.Active);
    }

    [Fact]
    public void Unallocated_DoesNotTakeAName()
    {
        var unallocated = Bucket.Unallocated(NewId(), Owner, Currency.Pln);

        Create("Unallocated", [unallocated]).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Rename_KeepsIdentityAndVersion()
    {
        var stored = StoredBuckets.Restore(NewId(), Owner, "Cushion", Currency.Pln, BucketKind.Savings, null, BucketStatus.Active, BucketSystemRole.None, version: 3);

        var renamed = stored.Rename("Safety net", [stored]).Value;

        renamed.Id.ShouldBe(stored.Id);
        renamed.Version.ShouldBe(3);
        renamed.Name.ShouldBe("Safety net");
        renamed.Currency.ShouldBe(stored.Currency);
        stored.Name.ShouldBe("Cushion");
    }

    [Fact]
    public void Rename_ChangingOnlyTheCase_IsAllowed()
    {
        var stored = Create("cushion").Value;

        stored.Rename("Cushion", [stored]).Value.Name.ShouldBe("Cushion");
    }

    [Fact]
    public void Rename_ToAnotherActiveBucketsName_IsNameTaken()
    {
        var first = Create("Cushion").Value;
        var second = Create("Holiday", [first]).Value;

        second.Rename("CUSHION", [first, second]).ShouldFailWith(BudgetErrorCodes.BucketNameTaken, ErrorKind.BusinessRule);
    }

    [Fact]
    public void Rename_OfASystemBucket_IsRejected() =>
        Bucket.Unallocated(NewId(), Owner, Currency.Pln).Rename("Mine", [])
            .ShouldFailWith(BudgetErrorCodes.BucketSystemBucket, ErrorKind.BusinessRule);

    [Fact]
    public void Rename_OfAnArchivedBucket_IsRejected() =>
        StoredBuckets.Restore(NewId(), Owner, "Old", Currency.Pln, BucketKind.Savings, null, BucketStatus.Archived, BucketSystemRole.None)
            .Rename("New", []).ShouldFailWith(BudgetErrorCodes.BucketArchived, ErrorKind.BusinessRule);

    [Theory]
    [InlineData("Cushion", "PLN", "Savings", null, true)]
    [InlineData("cushion", "PLN", "Savings", null, false)]
    [InlineData("Cushion", "EUR", "Savings", null, false)]
    [InlineData("Cushion", "PLN", "Investment", "Crypto", false)]
    public void HasDefinition_ComparesWhatCreateWrites(string name, string currency, string kind, string? profile, bool expected)
    {
        var bucket = Create("Cushion").Value;

        bucket.HasDefinition(name, Currency.FromCode(currency).Value, Enum.Parse<BucketKind>(kind), profile is null ? null : Enum.Parse<InvestmentProfile>(profile))
            .ShouldBe(expected);
    }

    [Theory]
    [InlineData(" Cushion")]
    [InlineData("")]
    [InlineData("Cush\tion")]
    public void Rename_ToAnInvalidName_IsABug(string name)
    {
        // Input is validated before it reaches the domain; an invalid name here means a caller skipped it.
        var bucket = Create("Cushion").Value;

        Should.Throw<ArgumentException>(() => bucket.Rename(name, [bucket]));
    }

    [Fact]
    public void Id_CannotBeEmpty() => Should.Throw<ArgumentException>(() => BucketId.From(Guid.Empty));

    [Fact]
    public void Ids_FromClient_MustBeVersion7()
    {
        StronglyTypedId.FromClient<BucketId>(Guid.CreateVersion7()).IsSuccess.ShouldBeTrue();
        StronglyTypedId.FromClient<BucketId>(Guid.NewGuid()).ShouldFailWith(ErrorCodes.IdInvalid, ErrorKind.Validation);
    }

    private static FluentResults.Result<Bucket> Create(string name, IReadOnlyCollection<Bucket>? existing = null) =>
        Bucket.Create(NewId(), Owner, name, Currency.Pln, BucketKind.Savings, null, existing ?? []);

    private static BucketId NewId() => BucketId.From(Guid.CreateVersion7());
}
