using FinTrack.Shared.Infrastructure.Mongo;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.IntegrationTests.Mongo.Collections;

/// <summary>
/// Owner isolation (invariant 12) and optimistic concurrency for mutable documents.
/// </summary>
public sealed class MutableCollectionTests(MongoDatabaseFixture fixture)
{
    private readonly FakeClock clock = new();
    private readonly TestOwnerContext owner = new(TestOwners.A);
    private readonly string collectionName = $"it_{Guid.NewGuid():N}";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Insert_StoresVersionOneAndTimestampsFromTheClock()
    {
        var collection = Collection();
        var bucket = TestBucket.New(TestOwners.A);

        var result = await collection.InsertAsync(bucket, cancellationToken: Cancellation);

        result.IsSuccess.ShouldBeTrue();
        var stored = await collection.GetAsync(bucket.Id, cancellationToken: Cancellation);
        stored.ShouldBe(bucket with { Version = 1, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow });
    }

    [Fact]
    public async Task Insert_DoesNotChangeTheDocumentObject()
    {
        var bucket = TestBucket.New(TestOwners.A);
        var copy = bucket with { };

        await Collection().InsertAsync(bucket, cancellationToken: Cancellation);

        bucket.ShouldBe(copy);
    }

    [Fact]
    public async Task Insert_OfAnotherOwnersDocument_ThrowsAndStoresNothing()
    {
        var collection = Collection();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            collection.InsertAsync(TestBucket.New(TestOwners.B), cancellationToken: Cancellation));

        (await RawCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Insert_WithNonZeroVersion_Throws()
    {
        var bucket = TestBucket.New(TestOwners.A) with { Version = 1 };

        await Should.ThrowAsync<ArgumentException>(() => Collection().InsertAsync(bucket, cancellationToken: Cancellation));
    }

    [Fact]
    public async Task Insert_WithEmptyId_Throws()
    {
        var bucket = TestBucket.New(TestOwners.A) with { Id = Guid.Empty };

        await Should.ThrowAsync<ArgumentException>(() => Collection().InsertAsync(bucket, cancellationToken: Cancellation));
    }

    [Fact]
    public async Task Insert_OfAnExistingId_Conflicts()
    {
        var collection = Collection();
        var bucket = TestBucket.New(TestOwners.A);
        await collection.InsertAsync(bucket, cancellationToken: Cancellation);

        var second = await collection.InsertAsync(bucket with { Name = "Car" }, cancellationToken: Cancellation);

        second.ShouldFailWith(ErrorCodes.ConcurrencyConflict, ErrorKind.Conflict);
        (await collection.GetAsync(bucket.Id, cancellationToken: Cancellation))!.Name.ShouldBe("Cushion");
    }

    [Fact]
    public async Task Insert_OfAnIdThatAnotherOwnerUses_Conflicts()
    {
        var collection = Collection();
        var bucketOfA = TestBucket.New(TestOwners.A);
        await collection.InsertAsync(bucketOfA, cancellationToken: Cancellation);

        owner.OwnerId = TestOwners.B;
        var result = await collection.InsertAsync(bucketOfA with { OwnerId = TestOwners.B }, cancellationToken: Cancellation);

        result.ShouldFailWith(ErrorCodes.ConcurrencyConflict, ErrorKind.Conflict);
        owner.OwnerId = TestOwners.A;
        (await collection.GetAsync(bucketOfA.Id, cancellationToken: Cancellation)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Reads_SeeOnlyTheCurrentOwnersDocuments()
    {
        var collection = Collection();
        var bucketOfA = TestBucket.New(TestOwners.A, "A's cushion");
        await collection.InsertAsync(bucketOfA, cancellationToken: Cancellation);
        owner.OwnerId = TestOwners.B;
        var bucketOfB = TestBucket.New(TestOwners.B, "B's cushion");
        await collection.InsertAsync(bucketOfB, cancellationToken: Cancellation);

        (await collection.GetAsync(bucketOfA.Id, cancellationToken: Cancellation)).ShouldBeNull();
        (await collection.FindAsync(_ => true, cancellationToken: Cancellation)).ShouldHaveSingleItem().Id.ShouldBe(bucketOfB.Id);
        (await collection.FindAsync(bucket => bucket.Id == bucketOfA.Id, cancellationToken: Cancellation)).ShouldBeEmpty();
        (await collection.FindAsync(bucket => bucket.OwnerId == TestOwners.A, cancellationToken: Cancellation)).ShouldBeEmpty();
        (await collection.FindAsync(FilterDefinition<TestBucket>.Empty, cancellationToken: Cancellation)).ShouldHaveSingleItem().Id.ShouldBe(bucketOfB.Id);
    }

    [Fact]
    public async Task Replace_WithTheCurrentVersion_RaisesTheVersionAndUpdatedAt()
    {
        var collection = Collection();
        var bucket = TestBucket.New(TestOwners.A);
        await collection.InsertAsync(bucket, cancellationToken: Cancellation);
        var createdAt = clock.UtcNow;
        var read = (await collection.GetAsync(bucket.Id, cancellationToken: Cancellation))!;
        clock.Advance(TimeSpan.FromMinutes(5));

        var result = await collection.ReplaceAsync(read with { Name = "Emergency cushion" }, cancellationToken: Cancellation);

        result.IsSuccess.ShouldBeTrue();
        var stored = await collection.GetAsync(bucket.Id, cancellationToken: Cancellation);
        stored.ShouldBe(read with { Name = "Emergency cushion", Version = 2, CreatedAt = createdAt, UpdatedAt = clock.UtcNow });
    }

    [Fact]
    public async Task Replace_WithAStaleVersion_ConflictsAndChangesNothing()
    {
        var collection = Collection();
        var bucket = TestBucket.New(TestOwners.A);
        await collection.InsertAsync(bucket, cancellationToken: Cancellation);
        var first = (await collection.GetAsync(bucket.Id, cancellationToken: Cancellation))!;
        var second = first with { };
        (await collection.ReplaceAsync(first with { Name = "Car" }, cancellationToken: Cancellation)).IsSuccess.ShouldBeTrue();

        var stale = await collection.ReplaceAsync(second with { Name = "Vacation" }, cancellationToken: Cancellation);

        stale.ShouldFailWith(ErrorCodes.ConcurrencyConflict, ErrorKind.Conflict);
        var stored = (await collection.GetAsync(bucket.Id, cancellationToken: Cancellation))!;
        stored.Name.ShouldBe("Car");
        stored.Version.ShouldBe(2);
    }

    [Fact]
    public async Task Replace_OfADocumentThatIsNotStored_Conflicts()
    {
        var bucket = TestBucket.New(TestOwners.A) with { Version = 1 };

        var result = await Collection().ReplaceAsync(bucket, cancellationToken: Cancellation);

        result.ShouldFailWith(ErrorCodes.ConcurrencyConflict, ErrorKind.Conflict);
        (await RawCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Replace_OfAnotherOwnersDocument_ConflictsAndChangesNothing()
    {
        var collection = Collection();
        var bucketOfA = TestBucket.New(TestOwners.A);
        await collection.InsertAsync(bucketOfA, cancellationToken: Cancellation);

        owner.OwnerId = TestOwners.B;
        var result = await collection.ReplaceAsync(
            bucketOfA with { OwnerId = TestOwners.B, Version = 1, Name = "Taken over" },
            cancellationToken: Cancellation);

        result.ShouldFailWith(ErrorCodes.ConcurrencyConflict, ErrorKind.Conflict);
        owner.OwnerId = TestOwners.A;
        (await collection.GetAsync(bucketOfA.Id, cancellationToken: Cancellation))!.Name.ShouldBe("Cushion");
    }

    [Fact]
    public async Task Replace_WithADocumentOfAnotherOwner_Throws()
    {
        var collection = Collection();
        var bucket = TestBucket.New(TestOwners.A);
        await collection.InsertAsync(bucket, cancellationToken: Cancellation);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            collection.ReplaceAsync(bucket with { OwnerId = TestOwners.B, Version = 1 }, cancellationToken: Cancellation));
    }

    [Fact]
    public async Task Replace_WithVersionZero_Throws()
    {
        var bucket = TestBucket.New(TestOwners.A);

        await Should.ThrowAsync<ArgumentException>(() => Collection().ReplaceAsync(bucket, cancellationToken: Cancellation));
    }

    [Fact]
    public async Task ConcurrentReplaces_OfTheSameVersion_ExactlyOneSucceeds()
    {
        var collection = Collection();
        var bucket = TestBucket.New(TestOwners.A);
        await collection.InsertAsync(bucket, cancellationToken: Cancellation);
        var read = (await collection.GetAsync(bucket.Id, cancellationToken: Cancellation))!;

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(writer =>
            collection.ReplaceAsync(read with { Name = $"Writer {writer}" }, cancellationToken: Cancellation)));

        results.Count(result => result.IsSuccess).ShouldBe(1);
        results.Where(result => result.IsFailed).ShouldAllBe(result => result.Errors.Single().Message == ErrorCodes.ConcurrencyConflict);
        (await collection.GetAsync(bucket.Id, cancellationToken: Cancellation))!.Version.ShouldBe(2);
    }

    [Fact]
    public async Task Delete_WithTheCurrentVersion_RemovesTheDocument()
    {
        var collection = Collection();
        var bucket = TestBucket.New(TestOwners.A);
        await collection.InsertAsync(bucket, cancellationToken: Cancellation);

        var result = await collection.DeleteAsync(bucket.Id, expectedVersion: 1, cancellationToken: Cancellation);

        result.IsSuccess.ShouldBeTrue();
        (await RawCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Delete_WithAStaleVersion_ConflictsAndKeepsTheDocument()
    {
        var collection = Collection();
        var bucket = TestBucket.New(TestOwners.A);
        await collection.InsertAsync(bucket, cancellationToken: Cancellation);
        await collection.ReplaceAsync(bucket with { Version = 1, Name = "Car" }, cancellationToken: Cancellation);

        var result = await collection.DeleteAsync(bucket.Id, expectedVersion: 1, cancellationToken: Cancellation);

        result.ShouldFailWith(ErrorCodes.ConcurrencyConflict, ErrorKind.Conflict);
        (await RawCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Delete_OfAnotherOwnersDocument_ConflictsAndKeepsTheDocument()
    {
        var collection = Collection();
        var bucketOfA = TestBucket.New(TestOwners.A);
        await collection.InsertAsync(bucketOfA, cancellationToken: Cancellation);

        owner.OwnerId = TestOwners.B;
        var result = await collection.DeleteAsync(bucketOfA.Id, expectedVersion: 1, cancellationToken: Cancellation);

        result.ShouldFailWith(ErrorCodes.ConcurrencyConflict, ErrorKind.Conflict);
        (await RawCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Writes_WorkInsideASessionTransaction()
    {
        var collection = Collection();
        var bucket = TestBucket.New(TestOwners.A);
        await collection.InsertAsync(TestBucket.New(TestOwners.A, "Created first"), cancellationToken: Cancellation);

        using (var session = await fixture.Client.StartSessionAsync(cancellationToken: Cancellation))
        {
            session.StartTransaction();
            (await collection.InsertAsync(bucket, session, Cancellation)).IsSuccess.ShouldBeTrue();
            (await collection.GetAsync(bucket.Id, session, Cancellation)).ShouldNotBeNull();
            await session.AbortTransactionAsync(Cancellation);
        }

        (await collection.GetAsync(bucket.Id, cancellationToken: Cancellation)).ShouldBeNull();
    }

    private MutableCollection<TestBucket> Collection() => new(fixture.Database, collectionName, owner, clock);

    private Task<long> RawCountAsync() =>
        fixture.Database.GetCollection<BsonDocument>(collectionName)
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Cancellation);
}
