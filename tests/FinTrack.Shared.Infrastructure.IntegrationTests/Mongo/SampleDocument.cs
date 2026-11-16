using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Persistence;

namespace FinTrack.Shared.Infrastructure.IntegrationTests.Mongo;

/// <summary>A document with one field of every stored kind. All numbers are invented.</summary>
internal sealed record SampleDocument : IMutableDocument
{
    public required Guid Id { get; init; }

    public required OwnerId OwnerId { get; init; }

    public required int SchemaVersion { get; init; }

    public required long Version { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public required SampleKind Kind { get; init; }

    public SampleKind? OptionalKind { get; init; }

    public required DateOnly OccurredOn { get; init; }

    public required Money Balance { get; init; }

    public required Currency Currency { get; init; }

    public required decimal Rate { get; init; }

    public decimal? OptionalRate { get; init; }

    public SampleId? ParentId { get; init; }

    /// <summary>A fully populated sample; tests change single fields with <c>with</c>.</summary>
    public static SampleDocument Create() => new()
    {
        Id = Guid.Parse("019a8f3e-5b2c-7d41-9a6e-3c5d7e9f1a2b"),
        OwnerId = OwnerId.From(Guid.Parse("6f1d2c3b-4a59-4e8d-b7c6-a5f4e3d2c1b0")),
        SchemaVersion = 1,
        Version = 3,
        CreatedAt = new DateTimeOffset(2026, 1, 15, 9, 30, 0, 123, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 2, 1, 18, 5, 59, 999, TimeSpan.Zero),
        Kind = SampleKind.Investment,
        OptionalKind = SampleKind.Savings,
        OccurredOn = new DateOnly(2026, 1, 15),
        Balance = Money.From(1234.56m, Currency.Pln),
        Currency = Currency.Eur,
        Rate = 4.3215m,
        OptionalRate = 0.000000000000000001m,
        ParentId = SampleId.From(Guid.Parse("019a8f3e-5b2c-7d41-8a6e-000000000001")),
    };
}

internal enum SampleKind
{
    Savings,
    Investment,
}

internal readonly record struct SampleId : IStronglyTypedId<SampleId>
{
    private SampleId(Guid value) => Value = value;

    public Guid Value { get; }

    public static SampleId From(Guid value) =>
        value == Guid.Empty ? throw new ArgumentException("Empty.", nameof(value)) : new SampleId(value);
}

/// <summary>A document holding one plain decimal, for exact round trips.</summary>
internal sealed record DecimalDocument(Guid Id, int Index, decimal Value);

/// <summary>A document whose identifiers are generated in sequence, for ordering tests.</summary>
internal sealed record SequencedDocument(Guid Id, int Sequence);
