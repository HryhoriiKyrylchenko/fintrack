using FinTrack.Shared.Kernel.Ids;
using FinTrack.Tests.Common;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.IntegrationTests.Mongo;

/// <summary>
/// UUIDv7 identifiers start with their creation time. Stored as standard UUIDs (big-endian
/// bytes), the server sorts them in creation order, so <c>_id</c> also orders documents in time.
/// </summary>
public sealed class UuidOrderingTests(MongoDatabaseFixture fixture)
{
    private const int Count = 300;

    [Fact]
    public async Task SortingById_FollowsCreationOrder()
    {
        var clock = new FakeClock();
        var generator = new IdGenerator(clock);
        var documents = new List<SequencedDocument>();
        for (var sequence = 0; sequence < Count; sequence++)
        {
            documents.Add(new SequencedDocument(generator.New<SampleId>().Value, sequence));
            clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        var (typed, _) = fixture.NewCollection<SequencedDocument>();
        await typed.InsertManyAsync(documents.OrderBy(document => document.Id.GetHashCode()), cancellationToken: TestContext.Current.CancellationToken);

        var sorted = await typed.Find(FilterDefinition<SequencedDocument>.Empty)
            .SortBy(document => document.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        sorted.Select(document => document.Sequence).ShouldBe(Enumerable.Range(0, Count));
    }

    [Fact]
    public async Task RangeQueryById_SelectsByCreationTime()
    {
        var clock = new FakeClock();
        var generator = new IdGenerator(clock);
        var early = new SequencedDocument(generator.New<SampleId>().Value, 0);
        clock.Advance(TimeSpan.FromSeconds(1));
        var boundary = generator.New<SampleId>().Value;
        clock.Advance(TimeSpan.FromSeconds(1));
        var late = new SequencedDocument(generator.New<SampleId>().Value, 1);

        var (typed, _) = fixture.NewCollection<SequencedDocument>();
        await typed.InsertManyAsync([late, early], cancellationToken: TestContext.Current.CancellationToken);

        var after = await typed.Find(Builders<SequencedDocument>.Filter.Gt(document => document.Id, boundary))
            .ToListAsync(TestContext.Current.CancellationToken);

        after.ShouldHaveSingleItem().Sequence.ShouldBe(1);
    }
}
