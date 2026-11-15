namespace FinTrack.Shared.Kernel.Ids;

/// <summary>
/// An identifier with its own type (for example <c>BucketId</c>), so that two kinds of
/// identifier can never be mixed up. Every identifier wraps a non-empty <see cref="Guid"/>.
/// </summary>
/// <remarks>
/// Pattern for a new identifier: a <c>readonly record struct</c> with a private constructor,
/// a <c>Value</c> property and a static <see cref="From"/> that rejects <see cref="Guid.Empty"/>.
/// See <see cref="OwnerId"/>. Values typed by a user or sent by a client go through
/// <see cref="StronglyTypedId.FromClient{TId}"/> instead.
/// </remarks>
/// <typeparam name="TSelf">The identifier type itself.</typeparam>
public interface IStronglyTypedId<TSelf>
    where TSelf : struct, IStronglyTypedId<TSelf>
{
    /// <summary>The wrapped value.</summary>
    Guid Value { get; }

    /// <summary>
    /// Wraps a trusted value, for example one read from the database or just generated.
    /// Throws <see cref="ArgumentException"/> for <see cref="Guid.Empty"/>, which is always a bug.
    /// </summary>
    static abstract TSelf From(Guid value);
}
