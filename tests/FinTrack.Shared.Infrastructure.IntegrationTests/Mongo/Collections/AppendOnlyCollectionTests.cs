using System.Reflection;
using FinTrack.Shared.Infrastructure.Mongo;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.IntegrationTests.Mongo.Collections;

/// <summary>
/// Append-only documents (rule 5): insert and read only, scoped by owner (invariant 12).
/// </summary>
public sealed class AppendOnlyCollectionTests(MongoDatabaseFixture fixture)
{
    private readonly FakeClock clock = new();
    private readonly TestOwnerContext owner = new(TestOwners.A);
    private readonly string collectionName = $"it_{Guid.NewGuid():N}";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Insert_StoresTheDocumentAsItIs()
    {
        var collection = Collection();
        var entry = TestEntry.New(TestOwners.A, clock.UtcNow);

        (await collection.InsertAsync(entry, cancellationToken: Cancellation)).IsSuccess.ShouldBeTrue();

        (await collection.GetAsync(entry.Id, cancellationToken: Cancellation)).ShouldBe(entry);
    }

    [Fact]
    public async Task Insert_OfAnotherOwnersDocument_Throws()
    {
        await Should.ThrowAsync<InvalidOperationException>(() =>
            Collection().InsertAsync(TestEntry.New(TestOwners.B, clock.UtcNow), cancellationToken: Cancellation));

        (await Collection().FindAsync(_ => true, cancellationToken: Cancellation)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Insert_OfAnExistingId_ConflictsAndKeepsTheFirst()
    {
        var collection = Collection();
        var entry = TestEntry.New(TestOwners.A, clock.UtcNow, "first");
        await collection.InsertAsync(entry, cancellationToken: Cancellation);

        var result = await collection.InsertAsync(entry with { Note = "second" }, cancellationToken: Cancellation);

        result.ShouldFailWith(ErrorCodes.ConcurrencyConflict, ErrorKind.Conflict);
        (await collection.GetAsync(entry.Id, cancellationToken: Cancellation))!.Note.ShouldBe("first");
    }

    [Fact]
    public async Task Find_SeesOnlyTheCurrentOwnersDocuments_InTheRequestedOrder()
    {
        var collection = Collection();
        var first = TestEntry.New(TestOwners.A, clock.UtcNow, "first");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var second = TestEntry.New(TestOwners.A, clock.UtcNow, "second");
        await collection.InsertAsync(second, cancellationToken: Cancellation);
        await collection.InsertAsync(first, cancellationToken: Cancellation);
        owner.OwnerId = TestOwners.B;
        await collection.InsertAsync(TestEntry.New(TestOwners.B, clock.UtcNow, "of B"), cancellationToken: Cancellation);
        owner.OwnerId = TestOwners.A;

        var found = await collection.FindAsync(
            _ => true,
            Builders<TestEntry>.Sort.Ascending(entry => entry.RecordedAt),
            cancellationToken: Cancellation);

        found.Select(entry => entry.Note).ShouldBe(["first", "second"]);
    }

    [Fact]
    public void AppendOnlyCollection_HasNoWayToChangeOrRemoveADocument()
    {
        var writeMethods = typeof(AppendOnlyCollection<TestEntry>)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.DeclaringType != typeof(object))
            .Select(method => method.Name)
            .Where(name => !name.StartsWith("Find", StringComparison.Ordinal) && !name.StartsWith("Get", StringComparison.Ordinal))
            .Distinct();

        writeMethods.ShouldBe(["InsertAsync"]);
    }

    [Theory]
    [InlineData(typeof(AppendOnlyCollection<TestEntry>))]
    [InlineData(typeof(MutableCollection<TestBucket>))]
    public void Collections_NeverExposeTheUnderlyingMongoCollection(Type collectionType)
    {
        var exposed = collectionType
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(member => member switch
            {
                PropertyInfo property => property.PropertyType,
                FieldInfo field => field.FieldType,
                MethodInfo method => method.ReturnType,
                _ => null,
            })
            .Where(type => type is not null && type.ToString().Contains("IMongoCollection", StringComparison.Ordinal));

        exposed.ShouldBeEmpty();
    }

    private AppendOnlyCollection<TestEntry> Collection() => new(fixture.Database, collectionName, owner);
}
