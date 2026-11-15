using FinTrack.Shared.Kernel.Events;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Persistence;

namespace FinTrack.Shared.Tests.Ledger;

/// <summary>Minimal documents and events for testing the ledger contracts. Invented values only.</summary>
internal static class TestDocuments
{
    public static readonly OwnerId Owner = OwnerId.From(Guid.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b"));

    public static readonly DateTimeOffset Now = new(2026, 1, 15, 9, 30, 0, TimeSpan.Zero);

    public static TestEntry Entry() => new(Guid.CreateVersion7(Now), Owner, 1, Now);

    public static TestAggregate Aggregate(long version = 0) => new(Guid.CreateVersion7(Now), Owner, 1, version, Now, Now);

    public static TestEvent Event() => new(Guid.CreateVersion7(Now), Owner, Now);
}

internal sealed record TestEntry(Guid Id, OwnerId OwnerId, int SchemaVersion, DateTimeOffset RecordedAt) : IAppendOnlyDocument;

internal sealed record TestAggregate(Guid Id, OwnerId OwnerId, int SchemaVersion, long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
    : IMutableDocument;

[EventName("test.something_happened")]
internal sealed record TestEvent(Guid EventId, OwnerId OwnerId, DateTimeOffset OccurredAt) : IIntegrationEvent;
