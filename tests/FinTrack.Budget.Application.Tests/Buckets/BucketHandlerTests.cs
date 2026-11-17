using FinTrack.Budget.Application.Buckets;
using FinTrack.Budget.Contracts.V1.Buckets;
using FinTrack.Budget.Domain;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;
using DomainBucket = FinTrack.Budget.Domain.Buckets.Bucket;
using DomainKind = FinTrack.Budget.Domain.Buckets.BucketKind;
using DomainProfile = FinTrack.Budget.Domain.Buckets.InvestmentProfile;
using DomainStatus = FinTrack.Budget.Domain.Buckets.BucketStatus;

namespace FinTrack.Budget.Application.Tests.Buckets;

/// <summary>
/// Creating, renaming and listing buckets against in-memory fakes: version read first, one commit
/// per real change, nothing written for a repeat (invariant 13), rules from the domain.
/// </summary>
public class BucketHandlerTests
{
    private readonly CallLog log = new();
    private readonly FakeLedger ledger;
    private readonly FakeBudgetStore store;

    public BucketHandlerTests()
    {
        ledger = new FakeLedger(log);
        store = new FakeBudgetStore(log);
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_CommitsANewBucket_UnderTheVersionReadFirst()
    {
        ledger.Version = 5;
        var id = Guid.CreateVersion7();

        var result = await Create(new CreateBucket(id, "Cushion", Currency.Pln, BucketKind.Savings, null));

        result.IsSuccess.ShouldBeTrue();
        log.Calls.ShouldBe(["version", "settings", "buckets", "journal", "commit"]);
        var commit = ledger.Commits.ShouldHaveSingleItem();
        commit.ExpectedVersion.ShouldBe(5);
        var bucket = commit.Saves.ShouldHaveSingleItem().ShouldBeOfType<DomainBucket>();
        bucket.Id.ShouldBe(id);
        bucket.OwnerId.ShouldBe(FakeOwnerContext.Owner);
        bucket.Name.ShouldBe("Cushion");
        bucket.Kind.ShouldBe(DomainKind.Savings);
        bucket.Profile.ShouldBeNull();
        bucket.Version.ShouldBe(0);
    }

    [Fact]
    public async Task Create_Investment_MapsKindAndProfile()
    {
        await Create(new CreateBucket(Guid.CreateVersion7(), "Stocks", Currency.Usd, BucketKind.Investment, InvestmentProfile.RealValueAssets));

        var bucket = ledger.Commits.ShouldHaveSingleItem().Saves.ShouldHaveSingleItem().ShouldBeOfType<DomainBucket>();
        bucket.Kind.ShouldBe(DomainKind.Investment);
        bucket.Profile.ShouldBe(DomainProfile.RealValueAssets);
    }

    [Fact]
    public async Task Create_WithAnIdThatIsNotVersion7_IsIdInvalid()
    {
        var result = await Create(new CreateBucket(Guid.NewGuid(), "Cushion", Currency.Pln, BucketKind.Savings, null));

        result.ShouldFailWith(ErrorCodes.IdInvalid, ErrorKind.Validation);
        ledger.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task RepeatedCreate_WithTheSameValues_SucceedsAndWritesNothing()
    {
        var command = new CreateBucket(Guid.CreateVersion7(), "Cushion", Currency.Pln, BucketKind.Savings, null);
        store.Buckets.Add(Stored(command.Id, "Cushion", Currency.Pln));

        var result = await Create(command);

        result.IsSuccess.ShouldBeTrue();
        log.Calls.ShouldNotContain("commit");
    }

    [Theory]
    [InlineData("cushion", "PLN")]
    [InlineData("Cushion", "EUR")]
    public async Task RepeatedCreate_WithOtherValues_IsIdReused(string name, string currency)
    {
        var id = Guid.CreateVersion7();
        store.Buckets.Add(Stored(id, "Cushion", Currency.Pln));

        var result = await Create(new CreateBucket(id, name, Currency.FromCode(currency).Value, BucketKind.Savings, null));

        result.ShouldFailWith(BudgetErrorCodes.BucketIdReused, ErrorKind.Conflict);
        log.Calls.ShouldNotContain("commit");
    }

    [Fact]
    public async Task Create_WithATakenName_IsNameTaken()
    {
        store.Buckets.Add(Stored(Guid.CreateVersion7(), "Cushion", Currency.Pln));

        var result = await Create(new CreateBucket(Guid.CreateVersion7(), "CUSHION", Currency.Eur, BucketKind.Savings, null));

        result.ShouldFailWith(BudgetErrorCodes.BucketNameTaken, ErrorKind.BusinessRule);
        log.Calls.ShouldNotContain("commit");
    }

    [Fact]
    public async Task Rename_ReplacesTheStoredBucket()
    {
        var id = Guid.CreateVersion7();
        store.Buckets.Add(Stored(id, "Cushion", Currency.Pln, version: 4));

        var result = await Rename(new RenameBucket(id, "Safety net"));

        result.IsSuccess.ShouldBeTrue();
        log.Calls.ShouldBe(["version", "settings", "buckets", "journal", "commit"]);
        var renamed = ledger.Commits.ShouldHaveSingleItem().Saves.ShouldHaveSingleItem().ShouldBeOfType<DomainBucket>();
        renamed.Id.ShouldBe(id);
        renamed.Version.ShouldBe(4);
        renamed.Name.ShouldBe("Safety net");
    }

    [Fact]
    public async Task Rename_ToTheCurrentName_WritesNothing()
    {
        var id = Guid.CreateVersion7();
        store.Buckets.Add(Stored(id, "Cushion", Currency.Pln));

        (await Rename(new RenameBucket(id, "Cushion"))).IsSuccess.ShouldBeTrue();

        log.Calls.ShouldNotContain("commit");
    }

    [Fact]
    public async Task Rename_OfAnUnknownBucket_IsNotFound() =>
        (await Rename(new RenameBucket(Guid.CreateVersion7(), "Anything"))).ShouldFailWith(BudgetErrorCodes.BucketNotFound, ErrorKind.NotFound);

    [Fact]
    public async Task Rename_OfTheSystemBucket_IsRejected()
    {
        var unallocated = DomainBucket.Unallocated(Domain.Buckets.BucketId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, Currency.Pln);
        store.Buckets.Add(unallocated);

        (await Rename(new RenameBucket(unallocated.Id, "Mine"))).ShouldFailWith(BudgetErrorCodes.BucketSystemBucket, ErrorKind.BusinessRule);
        log.Calls.ShouldNotContain("commit");
    }

    [Fact]
    public async Task Get_ListsActiveBucketsAsCreated_WithoutArchived()
    {
        var older = Stored(Guid.CreateVersion7(), "Older", Currency.Pln, createdAt: FakeClock.DefaultStart);
        var newer = Stored(Guid.CreateVersion7(), "Newer", Currency.Eur, createdAt: FakeClock.DefaultStart.AddDays(1));
        var archived = Stored(Guid.CreateVersion7(), "Gone", Currency.Pln, status: DomainStatus.Archived);
        store.Buckets.AddRange([newer, archived, older]);

        var buckets = (await Get(new GetBuckets())).Value;

        buckets.Select(bucket => bucket.Name).ShouldBe(["Older", "Newer"]);
        buckets[1].ShouldBe(new BucketDto(newer.Id, "Newer", Currency.Eur, BucketKind.Savings, null, BucketStatus.Active, BucketSystemRole.None));
    }

    [Fact]
    public async Task Get_WithArchived_ListsThemToo()
    {
        store.Buckets.Add(Stored(Guid.CreateVersion7(), "Gone", Currency.Pln, status: DomainStatus.Archived));

        var bucket = (await Get(new GetBuckets(IncludeArchived: true))).Value.ShouldHaveSingleItem();

        bucket.Status.ShouldBe(BucketStatus.Archived);
    }

    [Fact]
    public async Task Get_ShowsTheSystemBucketWithoutName()
    {
        store.Buckets.Add(DomainBucket.Unallocated(Domain.Buckets.BucketId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, Currency.Usd));

        var bucket = (await Get(new GetBuckets())).Value.ShouldHaveSingleItem();

        bucket.Name.ShouldBeNull();
        bucket.SystemRole.ShouldBe(BucketSystemRole.Unallocated);
    }

    [Theory]
    [InlineData("", "Name", "NotEmptyValidator")]
    [InlineData(" Cushion", "Name", "NoSurroundingWhiteSpace")]
    [InlineData("Cush\tion", "Name", "NoControlCharacters")]
    public void CreateValidator_RejectsBadNames(string name, string field, string rule) =>
        new CreateBucketValidator().Validate(new CreateBucket(Guid.CreateVersion7(), name, Currency.Pln, BucketKind.Savings, null))
            .Errors.ShouldContain(error => error.PropertyName == field && error.ErrorCode == rule);

    [Fact]
    public void CreateValidator_RejectsALongName() =>
        new CreateBucketValidator().Validate(new CreateBucket(Guid.CreateVersion7(), new string('a', 61), Currency.Pln, BucketKind.Savings, null))
            .Errors.ShouldContain(error => error.PropertyName == "Name" && error.ErrorCode == "MaximumLengthValidator");

    [Theory]
    [InlineData(BucketKind.Investment, null, "NotNullValidator")]
    [InlineData(BucketKind.Savings, InvestmentProfile.Crypto, "NullValidator")]
    public void CreateValidator_ChecksTheProfileAgainstTheKind(BucketKind kind, InvestmentProfile? profile, string rule) =>
        new CreateBucketValidator().Validate(new CreateBucket(Guid.CreateVersion7(), "Cushion", Currency.Pln, kind, profile))
            .Errors.ShouldContain(error => error.PropertyName == "Profile" && error.ErrorCode == rule);

    [Fact]
    public void Validators_AcceptOrdinaryCommands()
    {
        new CreateBucketValidator().Validate(new CreateBucket(Guid.CreateVersion7(), "Crypto", Currency.Usd, BucketKind.Investment, InvestmentProfile.Crypto)).IsValid.ShouldBeTrue();
        new RenameBucketValidator().Validate(new RenameBucket(Guid.CreateVersion7(), "Safety net")).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void RenameValidator_UsesTheSameNameRules() =>
        new RenameBucketValidator().Validate(new RenameBucket(Guid.CreateVersion7(), "Cushion ")).Errors
            .ShouldContain(error => error.PropertyName == "Name" && error.ErrorCode == "NoSurroundingWhiteSpace");

    private Task<FluentResults.Result> Create(CreateBucket command) =>
        new CreateBucketHandler(new BudgetStateLoader(ledger, store), ledger, new FakeOwnerContext()).Handle(command, Cancellation).AsTask();

    private Task<FluentResults.Result> Rename(RenameBucket command) =>
        new RenameBucketHandler(new BudgetStateLoader(ledger, store), ledger).Handle(command, Cancellation).AsTask();

    private Task<FluentResults.Result<IReadOnlyList<BucketDto>>> Get(GetBuckets query) =>
        new GetBucketsHandler(new BudgetStateLoader(ledger, store)).Handle(query, Cancellation).AsTask();

    private static DomainBucket Stored(
        Guid id,
        string name,
        Currency currency,
        long version = 1,
        DateTimeOffset? createdAt = null,
        DomainStatus status = DomainStatus.Active) =>
        StoredBuckets.Of(id, name, currency, version, createdAt ?? FakeClock.DefaultStart, status);
}
