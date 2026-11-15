using FinTrack.Shared.Kernel.Time;

namespace FinTrack.Shared.Kernel.Ids;

/// <summary>
/// Creates new identifiers as version 7 UUIDs: the first 48 bits are the creation time
/// from <see cref="IClock"/>, the rest is random. Identifiers created in later milliseconds
/// sort after earlier ones; within one millisecond their order is random.
/// </summary>
public sealed class IdGenerator(IClock clock)
{
    /// <summary>Creates a new identifier of type <typeparamref name="TId"/>.</summary>
    public TId New<TId>()
        where TId : struct, IStronglyTypedId<TId> =>
        TId.From(Guid.CreateVersion7(clock.UtcNow));
}
