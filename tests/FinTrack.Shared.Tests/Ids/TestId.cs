using FinTrack.Shared.Kernel.Ids;

namespace FinTrack.Shared.Tests.Ids;

/// <summary>A document identifier for tests, built with the same pattern as real ones.</summary>
internal readonly record struct TestId : IStronglyTypedId<TestId>
{
    private TestId(Guid value) => Value = value;

    public Guid Value { get; }

    public static TestId From(Guid value) =>
        value == Guid.Empty ? throw new ArgumentException("An identifier cannot be empty.", nameof(value)) : new TestId(value);
}
