using FinTrack.Shared.Infrastructure.IntegrationTests.Ledger;
using FinTrack.Shared.Infrastructure.Outbox;
using FinTrack.Shared.Infrastructure.Owners;
using FinTrack.Shared.Kernel.Events;
using FinTrack.Shared.Kernel.Ids;
using Microsoft.Extensions.DependencyInjection;

namespace FinTrack.Shared.Infrastructure.IntegrationTests.Outbox;

/// <summary>Stored event names map back to exactly one type.</summary>
public sealed class IntegrationEventRegistryTests
{
    [Fact]
    public void RegisteredEvent_IsFoundByItsStableName()
    {
        var registry = Registry(services => services.AddIntegrationEvent<TestAccountOpened>().AddIntegrationEvent<TestAccountOpened>());

        registry.Find("testledger.account_opened").ShouldNotBeNull().EventType.ShouldBe(typeof(TestAccountOpened));
        registry.Find("testledger.unknown").ShouldBeNull();
    }

    [Fact]
    public void TwoTypesWithOneName_Throw() =>
        Should.Throw<InvalidOperationException>(() => Registry(services => services
            .AddIntegrationEvent<TestAccountOpened>()
            .AddIntegrationEvent<SameNameAsAccountOpened>()));

    [Fact]
    public void EventWithoutAName_Throws() =>
        Should.Throw<InvalidOperationException>(() => new ServiceCollection().AddIntegrationEvent<EventWithoutName>());

    [Fact]
    public void ScopedOwnerContext_IsSetOnce()
    {
        var context = new ScopedOwnerContext();
        var owner = OwnerId.From(Guid.CreateVersion7());

        Should.Throw<InvalidOperationException>(() => context.OwnerId);
        context.Set(owner);
        context.Set(owner);

        context.OwnerId.ShouldBe(owner);
        Should.Throw<InvalidOperationException>(() => context.Set(OwnerId.From(Guid.CreateVersion7())));
    }

    private static IntegrationEventRegistry Registry(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        register(services);
        return new IntegrationEventRegistry(services.BuildServiceProvider().GetServices<IntegrationEventRegistration>());
    }

    [EventName("testledger.account_opened")]
    private sealed record SameNameAsAccountOpened : IIntegrationEvent
    {
        public required Guid EventId { get; init; }

        public required OwnerId OwnerId { get; init; }

        public required DateTimeOffset OccurredAt { get; init; }
    }

    private sealed record EventWithoutName : IIntegrationEvent
    {
        public required Guid EventId { get; init; }

        public required OwnerId OwnerId { get; init; }

        public required DateTimeOffset OccurredAt { get; init; }
    }
}
