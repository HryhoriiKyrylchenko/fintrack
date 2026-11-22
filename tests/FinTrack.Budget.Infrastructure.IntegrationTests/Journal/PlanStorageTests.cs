using FinTrack.Budget.Application;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.Plans;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Time;
using FinTrack.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FinTrack.Budget.Infrastructure.IntegrationTests.Journal;

/// <summary>
/// The stored format of the plan entries (<c>PlanCreated</c>, <c>PlanAmountChanged</c>, <c>PlanEndChanged</c>) and of the optional
/// <c>planId</c> of deposits and transfers (ARCHITECTURE 2.2, 4.2, plan 1b B7, B9), element by element: months as <c>yyyy-MM</c>
/// strings, optional elements absent when empty, and stored 1a deposits and transfers still read unchanged.
/// </summary>
public sealed class PlanStorageTests(BudgetFixture fixture)
{
    private static readonly DateOnly Day = new(2026, 1, 15);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PlanCreated_HasThePinnedShape_WithItsEnd()
    {
        var owner = BudgetFixture.NewOwner();
        var bucket = BucketId.From(Guid.CreateVersion7());
        var created = PlanCreated.Create(NewId(), owner, bucket, Money.From(500m, Currency.Pln), new YearMonth(2026, 1), new YearMonth(2026, 12), Day, Day,
            new EntryInfo("Holiday fund", [], EntrySource.Manual), FakeClock.DefaultStart).Value;
        await AppendAsync(owner, created);

        (await RawAsync(created.Id)).ShouldBe(new BsonDocument
        {
            { "_id", Uuid(created.Id) },
            { "type", "PlanCreated" },
            { "ownerId", Uuid(owner.Value) },
            { "schemaVersion", 1 },
            { "recordedAt", new BsonDateTime(FakeClock.DefaultStart.UtcDateTime) },
            { "occurredOn", "2026-01-15" },
            { "note", "Holiday fund" },
            { "tags", new BsonArray() },
            { "source", "Manual" },
            { "bucketId", Uuid(bucket.Value) },
            { "amount", new BsonDocument { { "amount", new BsonDecimal128(500.00m) }, { "currency", "PLN" } } },
            { "validFrom", "2026-01" },
            { "validTo", "2026-12" },
        });
    }

    [Fact]
    public async Task PlanAmountChanged_HasThePinnedShape()
    {
        var owner = BudgetFixture.NewOwner();
        var plan = PlanId.From(Guid.CreateVersion7());
        var changed = PlanAmountChanged.Create(NewId(), owner, plan, Money.From(0.5m, Currency.Eur), new YearMonth(2026, 3), Day, Day, EntryInfo.None, FakeClock.DefaultStart).Value;
        await AppendAsync(owner, changed);

        (await RawAsync(changed.Id)).ShouldBe(new BsonDocument
        {
            { "_id", Uuid(changed.Id) },
            { "type", "PlanAmountChanged" },
            { "ownerId", Uuid(owner.Value) },
            { "schemaVersion", 1 },
            { "recordedAt", new BsonDateTime(FakeClock.DefaultStart.UtcDateTime) },
            { "occurredOn", "2026-01-15" },
            { "tags", new BsonArray() },
            { "source", "Manual" },
            { "planId", Uuid(plan.Value) },
            { "amount", new BsonDocument { { "amount", new BsonDecimal128(0.50m) }, { "currency", "EUR" } } },
            { "from", "2026-03" },
        });
    }

    [Fact]
    public async Task PlanEndChanged_HasThePinnedShape_AndWithoutAnEndNoValidTo()
    {
        var owner = BudgetFixture.NewOwner();
        var plan = PlanId.From(Guid.CreateVersion7());
        var ended = PlanEndChanged.Create(NewId(), owner, plan, new YearMonth(2025, 12), Day, Day, EntryInfo.None, FakeClock.DefaultStart).Value;
        var open = PlanEndChanged.Create(NewId(), owner, plan, null, Day, Day, EntryInfo.None, FakeClock.DefaultStart.AddMilliseconds(1)).Value;
        await AppendAsync(owner, ended, open);

        (await RawAsync(ended.Id)).ShouldBe(new BsonDocument
        {
            { "_id", Uuid(ended.Id) },
            { "type", "PlanEndChanged" },
            { "ownerId", Uuid(owner.Value) },
            { "schemaVersion", 1 },
            { "recordedAt", new BsonDateTime(FakeClock.DefaultStart.UtcDateTime) },
            { "occurredOn", "2026-01-15" },
            { "tags", new BsonArray() },
            { "source", "Manual" },
            { "planId", Uuid(plan.Value) },
            { "validTo", "2025-12" },
        });
        (await RawAsync(open.Id)).Names.ShouldNotContain("validTo");
    }

    [Fact]
    public async Task APlanWithoutAnEnd_HasNoValidTo_AndEveryPlanEntryReadsBackWithTheSameContent()
    {
        var owner = BudgetFixture.NewOwner();
        var created = PlanCreated.Create(NewId(), owner, BucketId.From(Guid.CreateVersion7()), Money.From(1m, Currency.Usd), new YearMonth(1, 1), null, Day, Day,
            new EntryInfo(null, ["a", "b"], EntrySource.Manual), FakeClock.DefaultStart).Value;
        var changed = PlanAmountChanged.Create(NewId(), owner, created.PlanId, Money.From(2m, Currency.Usd), new YearMonth(9999, 12), Day, Day, EntryInfo.None, FakeClock.DefaultStart.AddMilliseconds(1)).Value;
        var ended = PlanEndChanged.Create(NewId(), owner, created.PlanId, null, Day, Day, EntryInfo.None, FakeClock.DefaultStart.AddMilliseconds(2)).Value;
        await AppendAsync(owner, created, changed, ended);

        (await RawAsync(created.Id)).Names.ShouldNotContain("validTo");
        var read = await ReadAsync(owner);
        read.OfType<PlanCreated>().ShouldHaveSingleItem().HasSameContent(created).ShouldBeTrue();
        read.OfType<PlanAmountChanged>().ShouldHaveSingleItem().HasSameContent(changed).ShouldBeTrue();
        read.OfType<PlanEndChanged>().ShouldHaveSingleItem().HasSameContent(ended).ShouldBeTrue();
        read.OfType<PlanCreated>().Single().ValidTo.ShouldBeNull();
    }

    [Fact]
    public async Task ALinkedDepositAndTransfer_StoreTheirPlanLast_AndReadItBack()
    {
        var owner = BudgetFixture.NewOwner();
        var plan = PlanId.From(Guid.CreateVersion7());
        var deposit = Deposit.Create(NewId(), owner, BucketId.From(Guid.CreateVersion7()), Money.From(10m, Currency.Pln), Day, Day, EntryInfo.None, FakeClock.DefaultStart, plan).Value;
        var transfer = Transfer.Create(NewId(), owner, BucketId.From(Guid.CreateVersion7()), BucketId.From(Guid.CreateVersion7()), Money.From(10m, Currency.Pln),
            Money.From(10m, Currency.Pln), null, Day, Day, EntryInfo.None, FakeClock.DefaultStart.AddMilliseconds(1), plan).Value;
        await AppendAsync(owner, deposit, transfer);

        var storedDeposit = await RawAsync(deposit.Id);
        storedDeposit.Names.ShouldBe(["_id", "type", "ownerId", "schemaVersion", "recordedAt", "occurredOn", "tags", "source", "bucketId", "amount", "planId"]);
        storedDeposit["planId"].ShouldBe(Uuid(plan.Value));
        var storedTransfer = await RawAsync(transfer.Id);
        storedTransfer.Names.Last().ShouldBe("planId");
        storedTransfer["planId"].ShouldBe(Uuid(plan.Value));

        var read = await ReadAsync(owner);
        read.OfType<Deposit>().ShouldHaveSingleItem().PlanId.ShouldBe(plan);
        read.OfType<Transfer>().ShouldHaveSingleItem().PlanId.ShouldBe(plan);
        read.OfType<Deposit>().Single().HasSameContent(deposit).ShouldBeTrue();
        read.OfType<Transfer>().Single().HasSameContent(transfer).ShouldBeTrue();
    }

    [Fact]
    public async Task AOneTimeDepositAndTransfer_HaveNoPlanElement()
    {
        var owner = BudgetFixture.NewOwner();
        var deposit = Deposit.Create(NewId(), owner, BucketId.From(Guid.CreateVersion7()), Money.From(10m, Currency.Pln), Day, Day, EntryInfo.None, FakeClock.DefaultStart).Value;
        var transfer = Transfer.Create(NewId(), owner, BucketId.From(Guid.CreateVersion7()), BucketId.From(Guid.CreateVersion7()), Money.From(10m, Currency.Pln),
            Money.From(10m, Currency.Pln), null, Day, Day, EntryInfo.None, FakeClock.DefaultStart.AddMilliseconds(1)).Value;
        await AppendAsync(owner, deposit, transfer);

        (await RawAsync(deposit.Id)).Names.ShouldNotContain("planId");
        (await RawAsync(transfer.Id)).Names.ShouldNotContain("planId");
    }

    // Documents written in phase 1a, before plans existed, read unchanged: no upcaster.
    [Fact]
    public async Task StoredDepositAndTransferOf1a_ReadWithoutAPlan()
    {
        var owner = BudgetFixture.NewOwner();
        var money = new BsonDocument { { "amount", new BsonDecimal128(10.00m) }, { "currency", "PLN" } };
        var deposit = Common(owner, "Deposit");
        deposit.Add("bucketId", Uuid(Guid.CreateVersion7()));
        deposit.Add("amount", money);
        var transfer = Common(owner, "Transfer");
        transfer.Add("fromBucketId", Uuid(Guid.CreateVersion7()));
        transfer.Add("toBucketId", Uuid(Guid.CreateVersion7()));
        transfer.Add("fromAmount", money);
        transfer.Add("toAmount", money);
        transfer.Add("rate", new BsonDecimal128(1m));
        await fixture.Raw(BudgetCollections.Journal).InsertManyAsync([deposit, transfer], cancellationToken: Cancellation);

        var read = await ReadAsync(owner);

        read.OfType<Deposit>().ShouldHaveSingleItem().PlanId.ShouldBeNull();
        read.OfType<Transfer>().ShouldHaveSingleItem().PlanId.ShouldBeNull();
    }

    [Theory]
    [InlineData("PlanCreated", "bucketId")]
    [InlineData("PlanCreated", "amount")]
    [InlineData("PlanCreated", "validFrom")]
    [InlineData("PlanAmountChanged", "planId")]
    [InlineData("PlanAmountChanged", "amount")]
    [InlineData("PlanAmountChanged", "from")]
    [InlineData("PlanEndChanged", "planId")]
    public async Task StoredPlanEntry_WithoutARequiredElement_FailsToRead(string type, string element)
    {
        var owner = BudgetFixture.NewOwner();
        var document = PlanDocument(owner, type);
        document.Remove(element);
        await fixture.Raw(BudgetCollections.Journal).InsertOneAsync(document, cancellationToken: Cancellation);

        await Should.ThrowAsync<Exception>(() => ReadAsync(owner));
    }

    [Theory]
    [InlineData("PlanCreated", "validFrom", "2026-1")]
    [InlineData("PlanCreated", "validFrom", "2026-01-01")]
    [InlineData("PlanCreated", "validTo", "2026/12")]
    [InlineData("PlanAmountChanged", "from", "2026-13")]
    [InlineData("PlanEndChanged", "validTo", "0000-12")]
    public async Task StoredPlanEntry_WithAMonthInAnotherForm_FailsToRead(string type, string element, string month)
    {
        var owner = BudgetFixture.NewOwner();
        var document = PlanDocument(owner, type);
        document[element] = month;
        await fixture.Raw(BudgetCollections.Journal).InsertOneAsync(document, cancellationToken: Cancellation);

        await Should.ThrowAsync<FormatException>(() => ReadAsync(owner));
    }

    [Theory]
    [InlineData("PlanCreated")]
    [InlineData("PlanAmountChanged")]
    [InlineData("PlanEndChanged")]
    public async Task StoredPlanEntry_WrittenByHand_IsRead(string type)
    {
        var owner = BudgetFixture.NewOwner();
        await fixture.Raw(BudgetCollections.Journal).InsertOneAsync(PlanDocument(owner, type), cancellationToken: Cancellation);

        (await ReadAsync(owner)).ShouldHaveSingleItem().GetType().Name.ShouldBe(type);
    }

    private static JournalEntryId NewId() => JournalEntryId.From(Guid.CreateVersion7());

    private async Task AppendAsync(OwnerId owner, params JournalEntry[] entries)
    {
        await using var scope = fixture.ScopeFor(owner);
        var ledger = scope.ServiceProvider.GetRequiredService<ILedger>();
        var version = await ledger.GetVersionAsync(BudgetLedger.Name, Cancellation);
        (await ledger.CommitAsync(new LedgerCommit(BudgetLedger.Name, version, appends: entries), Cancellation)).IsSuccess.ShouldBeTrue();
    }

    private async Task<IReadOnlyList<JournalEntry>> ReadAsync(OwnerId owner)
    {
        await using var scope = fixture.ScopeFor(owner);
        return await scope.ServiceProvider.GetRequiredService<IBudgetStore>().GetJournalAsync(Cancellation);
    }

    private async Task<BsonDocument> RawAsync(Guid id) =>
        await fixture.Raw(BudgetCollections.Journal).Find(new BsonDocument("_id", Uuid(id))).SingleAsync(Cancellation);

    private static BsonBinaryData Uuid(Guid value) => new(value, GuidRepresentation.Standard);

    private static BsonDocument Common(OwnerId owner, string type) => new()
    {
        { "_id", Uuid(Guid.CreateVersion7()) },
        { "type", type },
        { "ownerId", Uuid(owner.Value) },
        { "schemaVersion", 1 },
        { "recordedAt", new BsonDateTime(FakeClock.DefaultStart.UtcDateTime) },
        { "occurredOn", "2026-01-15" },
        { "tags", new BsonArray() },
        { "source", "Manual" },
    };

    private static BsonDocument PlanDocument(OwnerId owner, string type)
    {
        var document = Common(owner, type);
        var money = new BsonDocument { { "amount", new BsonDecimal128(10.00m) }, { "currency", "PLN" } };
        switch (type)
        {
            case "PlanCreated":
                document.Add("bucketId", Uuid(Guid.CreateVersion7()));
                document.Add("amount", money);
                document.Add("validFrom", "2026-01");
                document.Add("validTo", "2026-12");
                break;
            case "PlanAmountChanged":
                document.Add("planId", Uuid(Guid.CreateVersion7()));
                document.Add("amount", money);
                document.Add("from", "2026-02");
                break;
            default:
                document.Add("planId", Uuid(Guid.CreateVersion7()));
                document.Add("validTo", "2026-06");
                break;
        }

        return document;
    }
}
