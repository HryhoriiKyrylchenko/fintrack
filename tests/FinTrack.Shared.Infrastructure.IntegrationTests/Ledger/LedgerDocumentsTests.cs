using FinTrack.Shared.Infrastructure.IntegrationTests.Mongo.Collections;
using FinTrack.Shared.Infrastructure.Ledger;
using FinTrack.Shared.Infrastructure.Mongo;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Persistence;
using FinTrack.Shared.Kernel.Time;
using FinTrack.Tests.Common;
using FinTrack.Shared.Infrastructure.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinTrack.Shared.Infrastructure.IntegrationTests.Ledger;

/// <summary>Which collection each document type of a ledger is stored in.</summary>
public sealed class LedgerDocumentsTests
{
    private static readonly LedgerName TestLedger = LedgerName.From("testledger");

    [Theory]
    [InlineData("journal")]
    [InlineData("otherledger_journal")]
    [InlineData("testledger2_journal")] // another ledger's name that starts with this one
    [InlineData("testledger_")]
    [InlineData("testledger_ledger_heads")]
    [InlineData("testledger_outbox")]
    [InlineData("testledger_processed_events")]
    [InlineData("testledger_Journal")]
    public void CollectionName_MustBeAFreeNameWithTheLedgerPrefix(string collectionName) =>
        Should.Throw<ArgumentException>(() => new LedgerDocuments(TestLedger).AppendOnly<TestJournalEntry>(collectionName));

    [Fact]
    public void SameTypeTwice_Throws() =>
        Should.Throw<ArgumentException>(() => new LedgerDocuments(TestLedger)
            .AppendOnly<TestJournalEntry>("testledger_journal")
            .AppendOnly<TestJournalEntry>("testledger_journal_copy"));

    [Fact]
    public void SameCollectionForTwoTypes_Throws() =>
        Should.Throw<ArgumentException>(() => new LedgerDocuments(TestLedger)
            .AppendOnly<TestJournalEntry>("testledger_journal")
            .Mutable<TestAccount>("testledger_journal"));

    [Fact]
    public void SameLedgerRegisteredTwice_Throws() =>
        Should.Throw<ArgumentException>(() => new LedgerCollections(
        [
            new LedgerDocuments(TestLedger).AppendOnly<TestJournalEntry>("testledger_journal"),
            new LedgerDocuments(TestLedger).Mutable<TestAccount>("testledger_accounts"),
        ]));

    [Fact]
    public void SameTypeInTwoLedgers_Throws() =>
        Should.Throw<ArgumentException>(() => new LedgerCollections(
        [
            new LedgerDocuments(TestLedger).AppendOnly<TestJournalEntry>("testledger_journal"),
            new LedgerDocuments(LedgerName.From("otherledger")).AppendOnly<TestJournalEntry>("otherledger_journal"),
        ]));

    [Fact]
    public void Subclass_IsStoredInItsBaseTypesCollection()
    {
        var collections = new LedgerCollections([new LedgerDocuments(TestLedger).AppendOnly<TestJournalEntry>("testledger_journal")]);

        collections.Find(typeof(TestDepositEntry)).ShouldNotBeNull().CollectionName.ShouldBe("testledger_journal");
        collections.Find(typeof(TestAccount)).ShouldBeNull();
    }

    [Fact]
    public void AddMongoLedger_RegistersAScopedLedgerAndTheIndexInitializer()
    {
        using var provider = new ServiceCollection()
            .AddFinTrackMongo(options =>
            {
                options.ConnectionString = "mongodb://localhost:27017/?replicaSet=rs0";
                options.DatabaseName = "fintrack";
            })
            .AddSingleton<IClock>(new FakeClock())
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddScoped<IOwnerContext>(_ => new TestOwnerContext(TestOwners.A))
            .AddMongoLedger()
            .AddLedgerDocuments(new LedgerDocuments(TestLedger).AppendOnly<TestJournalEntry>("testledger_journal"))
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        first.ServiceProvider.GetRequiredService<ILedger>().ShouldBeOfType<DispatchingLedger>()
            .ShouldNotBeSameAs(second.ServiceProvider.GetRequiredService<ILedger>());
        provider.GetServices<IMongoIndexInitializer>().ShouldContain(initializer => initializer is LedgerIndexInitializer);
        provider.GetRequiredService<LedgerCollections>().Contains(TestLedger).ShouldBeTrue();
    }

    [Fact]
    public void CollectionNames_FollowTheLedgerName()
    {
        LedgerCollections.HeadsCollection(TestLedger).ShouldBe("testledger_ledger_heads");
        LedgerCollections.OutboxCollection(TestLedger).ShouldBe("testledger_outbox");
        LedgerCollections.ProcessedEventsCollection(TestLedger).ShouldBe("testledger_processed_events");
    }
}
