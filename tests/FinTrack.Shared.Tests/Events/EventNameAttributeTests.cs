using FinTrack.Shared.Kernel.Events;
using FinTrack.Shared.Tests.Ledger;

namespace FinTrack.Shared.Tests.Events;

public class EventNameAttributeTests
{
    [Fact]
    public void Of_EventWithAttribute_ReturnsItsName() =>
        EventNameAttribute.Of(typeof(TestEvent)).ShouldBe("test.something_happened");

    [Fact]
    public void Of_EventWithoutAttribute_Throws() =>
        Should.Throw<InvalidOperationException>(() => EventNameAttribute.Of(typeof(UnnamedEvent)));

    [Theory]
    [InlineData("budget.deposit_added")]
    [InlineData("inv.cash_out_requested")]
    public void Constructor_StableName_IsAccepted(string name) => new EventNameAttribute(name).Name.ShouldBe(name);

    [Theory]
    [InlineData("DepositAdded")]
    [InlineData("budget")]
    [InlineData("budget.DepositAdded")]
    [InlineData("")]
    public void Constructor_OtherName_Throws(string name) =>
        Should.Throw<ArgumentException>(() => new EventNameAttribute(name));

    [Fact]
    public void Of_EventThatOnlyInheritsAName_Throws() =>
        // A stored event outlives refactorings: each type declares its own name, never its base type's.
        Should.Throw<InvalidOperationException>(() => EventNameAttribute.Of(typeof(DerivedEvent)));

    [EventName("test.base_happened")]
    private record BaseEvent(Guid EventId, Kernel.Ids.OwnerId OwnerId, DateTimeOffset OccurredAt) : IIntegrationEvent;

    private sealed record DerivedEvent(Guid EventId, Kernel.Ids.OwnerId OwnerId, DateTimeOffset OccurredAt) : BaseEvent(EventId, OwnerId, OccurredAt);

    private sealed record UnnamedEvent(Guid EventId, Kernel.Ids.OwnerId OwnerId, DateTimeOffset OccurredAt) : IIntegrationEvent;
}
