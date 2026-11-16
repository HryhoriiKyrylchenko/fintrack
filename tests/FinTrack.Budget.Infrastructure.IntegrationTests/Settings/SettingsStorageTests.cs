using System.Reflection;
using FinTrack.Budget.Application;
using FinTrack.Budget.Application.Settings;
using FinTrack.Budget.Domain.Settings;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace FinTrack.Budget.Infrastructure.IntegrationTests.Settings;

/// <summary>
/// The stored format of <see cref="OwnerSettings"/> in <c>budget_settings</c>, element by element.
/// It is permanent (ARCHITECTURE 2.2): a test that fails here means a change that needs an upcaster.
/// </summary>
public sealed class SettingsStorageTests(BudgetFixture fixture)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SavedSettings_HaveThePinnedShape()
    {
        var owner = BudgetFixture.NewOwner();
        await SaveAsync(owner, new SaveOwnerSettings(Currency.Pln, "Europe/Warsaw", "pl-PL"));

        var stored = await RawSettingsAsync(owner);

        var expected = new BsonDocument
        {
            { "_id", stored["_id"] },
            { "ownerId", new BsonBinaryData(owner.Value, GuidRepresentation.Standard) },
            { "schemaVersion", 1 },
            { "version", 1L },
            { "createdAt", new BsonDateTime(FakeClock.DefaultStart.UtcDateTime) },
            { "updatedAt", new BsonDateTime(FakeClock.DefaultStart.UtcDateTime) },
            { "reportingCurrency", "PLN" },
            { "timeZone", "Europe/Warsaw" },
            { "locale", "pl-PL" },
        };
        stored.ShouldBe(expected);
        stored["_id"].AsBsonBinaryData.SubType.ShouldBe(BsonBinarySubType.UuidStandard);
    }

    [Fact]
    public async Task SavedSettings_ReadBackWithTheStoredVersionAndTimes()
    {
        var owner = BudgetFixture.NewOwner();
        await SaveAsync(owner, new SaveOwnerSettings(Currency.Usd, "America/New_York", "en-US"));

        var read = await ReadAsync(owner);

        read.ShouldNotBeNull();
        read.OwnerId.ShouldBe(owner);
        read.Version.ShouldBe(1);
        read.CreatedAt.ShouldBe(FakeClock.DefaultStart);
        read.UpdatedAt.ShouldBe(FakeClock.DefaultStart);
        read.ReportingCurrency.ShouldBe(Currency.Usd);
        read.TimeZone.ShouldBe("America/New_York");
        read.Locale.ShouldBe("en-US");
    }

    [Fact]
    public void Mapping_StoresEveryProperty()
    {
        BudgetMongoMapping.Register();
        var mapped = BsonClassMap.LookupClassMap(typeof(OwnerSettings)).AllMemberMaps.Select(member => member.MemberName);

        var properties = typeof(OwnerSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(property => property.Name);

        mapped.ShouldBe(properties, ignoreOrder: true);
    }

    [Theory]
    [InlineData("an unknown element", "nickname", "x")]
    [InlineData("an unregistered currency", "reportingCurrency", "XYZ")]
    [InlineData("a number for a string", "timeZone", 1)]
    public async Task StoredDocument_ThatDoesNotFitTheFormat_FailsToRead(string reason, string element, object value)
    {
        var owner = BudgetFixture.NewOwner();
        var document = ValidDocument(owner);
        document[element] = BsonValue.Create(value);
        await fixture.Raw(BudgetCollections.Settings).InsertOneAsync(document, cancellationToken: Cancellation);

        await Should.ThrowAsync<FormatException>(() => ReadAsync(owner), reason);
    }

    [Theory]
    [InlineData("locale")]
    [InlineData("reportingCurrency")]
    [InlineData("version")]
    public async Task StoredDocument_WithAMissingElement_FailsToRead(string element)
    {
        var owner = BudgetFixture.NewOwner();
        var document = ValidDocument(owner);
        document.Remove(element);
        await fixture.Raw(BudgetCollections.Settings).InsertOneAsync(document, cancellationToken: Cancellation);

        // Every mapped element is required; the driver reports a missing one with its own exception type.
        await Should.ThrowAsync<BsonSerializationException>(() => ReadAsync(owner));
    }

    [Fact]
    public async Task SecondDocumentForTheSameOwner_IsRejectedByTheUniqueIndex()
    {
        var owner = BudgetFixture.NewOwner();
        await fixture.Raw(BudgetCollections.Settings).InsertOneAsync(ValidDocument(owner), cancellationToken: Cancellation);

        var second = ValidDocument(owner);

        var exception = await Should.ThrowAsync<MongoWriteException>(() =>
            fixture.Raw(BudgetCollections.Settings).InsertOneAsync(second, cancellationToken: Cancellation));
        exception.WriteError.Category.ShouldBe(ServerErrorCategory.DuplicateKey);
    }

    [Fact]
    public async Task UniqueIndex_IsOnTheStoredOwnerElement()
    {
        using var cursor = await fixture.Raw(BudgetCollections.Settings).Indexes.ListAsync(Cancellation);
        var indexes = await cursor.ToListAsync(Cancellation);

        var index = indexes.Single(candidate => candidate["name"] == "ownerId_unique");
        index["key"].AsBsonDocument.ShouldBe(new BsonDocument("ownerId", 1));
        index["unique"].AsBoolean.ShouldBeTrue();
    }

    private async Task SaveAsync(OwnerId owner, SaveOwnerSettings command)
    {
        await using var scope = fixture.ScopeFor(owner);
        var result = await ActivatorUtilities.CreateInstance<SaveOwnerSettingsHandler>(scope.ServiceProvider).Handle(command, Cancellation);
        result.IsSuccess.ShouldBeTrue();
    }

    private async Task<OwnerSettings?> ReadAsync(OwnerId owner)
    {
        await using var scope = fixture.ScopeFor(owner);
        return await scope.ServiceProvider.GetRequiredService<IBudgetStore>().GetSettingsAsync(Cancellation);
    }

    private async Task<BsonDocument> RawSettingsAsync(OwnerId owner) =>
        await fixture.Raw(BudgetCollections.Settings)
            .Find(new BsonDocument("ownerId", new BsonBinaryData(owner.Value, GuidRepresentation.Standard)))
            .SingleAsync(Cancellation);

    private static BsonDocument ValidDocument(OwnerId owner) => new()
    {
        { "_id", new BsonBinaryData(Guid.CreateVersion7(), GuidRepresentation.Standard) },
        { "ownerId", new BsonBinaryData(owner.Value, GuidRepresentation.Standard) },
        { "schemaVersion", 1 },
        { "version", 1L },
        { "createdAt", new BsonDateTime(FakeClock.DefaultStart.UtcDateTime) },
        { "updatedAt", new BsonDateTime(FakeClock.DefaultStart.UtcDateTime) },
        { "reportingCurrency", "PLN" },
        { "timeZone", "Europe/Warsaw" },
        { "locale", "pl-PL" },
    };
}
