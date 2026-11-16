using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Persistence;

namespace FinTrack.Shared.Infrastructure.IntegrationTests.Mongo.Collections;

/// <summary>A mutable document, shaped like a bucket.</summary>
internal sealed record TestBucket : IMutableDocument
{
    public required Guid Id { get; init; }

    public required OwnerId OwnerId { get; init; }

    public int SchemaVersion { get; init; } = 1;

    public long Version { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public required string Name { get; init; }

    public static TestBucket New(OwnerId owner, string name = "Cushion") =>
        new() { Id = Guid.CreateVersion7(), OwnerId = owner, Name = name };
}

/// <summary>An append-only document, shaped like a journal entry.</summary>
internal sealed record TestEntry : IAppendOnlyDocument
{
    public required Guid Id { get; init; }

    public required OwnerId OwnerId { get; init; }

    public int SchemaVersion { get; init; } = 1;

    public required DateTimeOffset RecordedAt { get; init; }

    public required string Note { get; init; }

    public static TestEntry New(OwnerId owner, DateTimeOffset recordedAt, string note = "entry") =>
        new() { Id = Guid.CreateVersion7(), OwnerId = owner, RecordedAt = recordedAt, Note = note };
}

/// <summary>An owner context that tests switch between owners.</summary>
internal sealed class TestOwnerContext(OwnerId owner) : IOwnerContext
{
    public OwnerId OwnerId { get; set; } = owner;
}

internal static class Owners
{
    public static readonly OwnerId A = OwnerId.From(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    public static readonly OwnerId B = OwnerId.From(Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002"));
}
