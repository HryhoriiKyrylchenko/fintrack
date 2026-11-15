using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Persistence;

namespace FinTrack.Shared.Tests.Ledger;

public class LedgerCommitTests
{
    private static readonly LedgerName Budget = LedgerName.From("budget");

    [Fact]
    public void Constructor_KeepsEverything()
    {
        var entry = TestDocuments.Entry();
        var aggregate = TestDocuments.Aggregate(version: 3);
        var integrationEvent = TestDocuments.Event();
        var handled = new ProcessedEvent("budget.on_cash_out", integrationEvent.EventId);

        var commit = new LedgerCommit(Budget, 7, [entry], [aggregate], [integrationEvent], handled);

        commit.Ledger.ShouldBe(Budget);
        commit.ExpectedVersion.ShouldBe(7);
        commit.Appends.ShouldBe([entry]);
        commit.Saves.ShouldBe([aggregate]);
        commit.Events.ShouldBe([integrationEvent]);
        commit.HandledEvent.ShouldBe(handled);
    }

    [Fact]
    public void Constructor_OmittedParts_AreEmpty()
    {
        var commit = new LedgerCommit(Budget, 0, appends: [TestDocuments.Entry()]);

        commit.Saves.ShouldBeEmpty();
        commit.Events.ShouldBeEmpty();
        commit.HandledEvent.ShouldBeNull();
    }

    [Fact]
    public void Constructor_CopiesTheLists_SoLaterChangesDoNotLeakIn()
    {
        var appends = new List<IAppendOnlyDocument> { TestDocuments.Entry() };

        var commit = new LedgerCommit(Budget, 0, appends);
        appends.Add(TestDocuments.Entry());

        commit.Appends.Count.ShouldBe(1);
    }

    [Fact]
    public void Constructor_OnlyAHandledEvent_IsAllowed() =>
        // A handler that decides there is nothing to do still records that it handled the event.
        new LedgerCommit(Budget, 0, handledEvent: new ProcessedEvent("budget.on_cash_out", Guid.CreateVersion7()))
            .HandledEvent.ShouldNotBeNull();

    [Fact]
    public void Constructor_NothingToWrite_Throws() =>
        Should.Throw<ArgumentException>(() => new LedgerCommit(Budget, 0));

    [Fact]
    public void Constructor_NegativeExpectedVersion_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new LedgerCommit(Budget, -1, [TestDocuments.Entry()]));

    [Fact]
    public void Constructor_DefaultLedgerName_Throws() =>
        Should.Throw<ArgumentException>(() => new LedgerCommit(default, 0, [TestDocuments.Entry()]));

    [Fact]
    public void Constructor_NullItem_Throws() =>
        Should.Throw<ArgumentException>(() => new LedgerCommit(Budget, 0, [TestDocuments.Entry(), null!]));

    [Fact]
    public void Constructor_SameDocumentIdTwice_Throws()
    {
        var entry = TestDocuments.Entry();

        Should.Throw<ArgumentException>(() => new LedgerCommit(Budget, 0, [entry, entry]));
    }

    [Fact]
    public void Constructor_SameIdInAppendsAndSaves_Throws()
    {
        var entry = TestDocuments.Entry();
        var aggregate = TestDocuments.Aggregate() with { Id = entry.Id };

        Should.Throw<ArgumentException>(() => new LedgerCommit(Budget, 0, [entry], [aggregate]));
    }

    [Fact]
    public void Constructor_SameEventTwice_Throws()
    {
        var integrationEvent = TestDocuments.Event();

        Should.Throw<ArgumentException>(() => new LedgerCommit(Budget, 0, [TestDocuments.Entry()], events: [integrationEvent, integrationEvent]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("OnCashOut")]
    public void ProcessedEvent_InvalidHandlerName_Throws(string handlerName) =>
        Should.Throw<ArgumentException>(() => new ProcessedEvent(handlerName, Guid.CreateVersion7()));

    [Fact]
    public void ProcessedEvent_EmptyEventId_Throws() =>
        Should.Throw<ArgumentException>(() => new ProcessedEvent("budget.on_cash_out", Guid.Empty));
}
