using FinTrack.Shared.Kernel.Events;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Persistence;

namespace FinTrack.Shared.Infrastructure.IntegrationTests.Ledger;

/// <summary>A journal entry; subclasses share one collection, as Budget entry types will.</summary>
internal record TestJournalEntry : IAppendOnlyDocument
{
    public required Guid Id { get; init; }

    public required OwnerId OwnerId { get; init; }

    public int SchemaVersion { get; init; } = 1;

    public required DateTimeOffset RecordedAt { get; init; }

    public required string Note { get; init; }
}

/// <summary>A subclass stored in the base type's collection.</summary>
internal sealed record TestDepositEntry : TestJournalEntry
{
    public required decimal Amount { get; init; }
}

/// <summary>A mutable document, shaped like a bucket.</summary>
internal sealed record TestAccount : IMutableDocument
{
    public required Guid Id { get; init; }

    public required OwnerId OwnerId { get; init; }

    public int SchemaVersion { get; init; } = 1;

    public long Version { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public required string Name { get; init; }
}

/// <summary>A journal entry of another context's ledger.</summary>
internal sealed record OtherLedgerEntry : IAppendOnlyDocument
{
    public required Guid Id { get; init; }

    public required OwnerId OwnerId { get; init; }

    public int SchemaVersion { get; init; } = 1;

    public required DateTimeOffset RecordedAt { get; init; }
}

/// <summary>A type that no ledger maps.</summary>
internal sealed record UnmappedEntry : IAppendOnlyDocument
{
    public required Guid Id { get; init; }

    public required OwnerId OwnerId { get; init; }

    public int SchemaVersion { get; init; } = 1;

    public required DateTimeOffset RecordedAt { get; init; }
}

[EventName("testledger.account_opened")]
internal sealed record TestAccountOpened : IIntegrationEvent
{
    public required Guid EventId { get; init; }

    public required OwnerId OwnerId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid AccountId { get; init; }
}
