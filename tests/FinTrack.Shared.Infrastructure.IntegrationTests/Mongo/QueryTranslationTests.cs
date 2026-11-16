using System.Linq.Expressions;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.IntegrationTests.Mongo;

/// <summary>
/// Filters written in C# are translated with the same serializers, so a query on a typed ID,
/// money, an enum or a date matches what was stored. Owner filters (step 11) depend on this.
/// </summary>
public sealed class QueryTranslationTests(MongoDatabaseFixture fixture)
{
    private static readonly OwnerId OwnerA = OwnerId.From(Guid.Parse("0f0e0d0c-0b0a-4908-8706-050403020100"));
    private static readonly OwnerId OwnerB = OwnerId.From(Guid.Parse("1f1e1d1c-1b1a-4918-9716-151413121110"));

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FilterByOwnerId_WithEqualityOperator() =>
        (await FindAsync(document => document.OwnerId == OwnerB)).ShouldBe([2]);

    [Fact]
    public async Task FilterByOwnerId_WithBuilder() =>
        (await FindAsync(Builders<SampleDocument>.Filter.Eq(document => document.OwnerId, OwnerA))).ShouldBe([1, 3]);

    [Fact]
    public async Task FilterByMoneyAmount() =>
        (await FindAsync(document => document.Balance.Amount > 100m)).ShouldBe([2, 3]);

    [Fact]
    public async Task FilterByMoneyCurrency() =>
        (await FindAsync(document => document.Balance.Currency == Currency.Eur)).ShouldBe([3]);

    [Fact]
    public async Task FilterByEnum() =>
        (await FindAsync(document => document.Kind == SampleKind.Savings)).ShouldBe([1, 2]);

    [Fact]
    public async Task FilterByBusinessDate() =>
        (await FindAsync(document => document.OccurredOn >= new DateOnly(2026, 2, 1))).ShouldBe([2, 3]);

    [Fact]
    public async Task FilterByInstant() =>
        (await FindAsync(document => document.CreatedAt < new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero))).ShouldBe([1]);

    private Task<int[]> FindAsync(Expression<Func<SampleDocument, bool>> filter) =>
        FindAsync(Builders<SampleDocument>.Filter.Where(filter));

    private async Task<int[]> FindAsync(FilterDefinition<SampleDocument> filter)
    {
        var (typed, _) = fixture.NewCollection<SampleDocument>();
        await typed.InsertManyAsync(Samples(), cancellationToken: Cancellation);

        var found = await typed.Find(filter).ToListAsync(Cancellation);
        return [.. found.Select(document => document.SchemaVersion).Order()];
    }

    // SchemaVersion doubles as a sample number, to keep the assertions short.
    private static IEnumerable<SampleDocument> Samples()
    {
        var sample = SampleDocument.Create();
        yield return sample with
        {
            Id = Guid.CreateVersion7(),
            SchemaVersion = 1,
            OwnerId = OwnerA,
            Kind = SampleKind.Savings,
            Balance = Money.From(50m, Currency.Pln),
            OccurredOn = new DateOnly(2026, 1, 15),
            CreatedAt = new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero),
        };
        yield return sample with
        {
            Id = Guid.CreateVersion7(),
            SchemaVersion = 2,
            OwnerId = OwnerB,
            Kind = SampleKind.Savings,
            Balance = Money.From(150m, Currency.Pln),
            OccurredOn = new DateOnly(2026, 2, 1),
            CreatedAt = new DateTimeOffset(2026, 2, 1, 9, 0, 0, TimeSpan.Zero),
        };
        yield return sample with
        {
            Id = Guid.CreateVersion7(),
            SchemaVersion = 3,
            OwnerId = OwnerA,
            Kind = SampleKind.Investment,
            Balance = Money.From(700.25m, Currency.Eur),
            OccurredOn = new DateOnly(2026, 3, 10),
            CreatedAt = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero),
        };
    }
}
