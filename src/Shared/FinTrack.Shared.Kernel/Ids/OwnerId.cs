namespace FinTrack.Shared.Kernel.Ids;

/// <summary>
/// The user who owns a document. Every query and write is filtered by it (CLAUDE.md rule 9).
/// </summary>
/// <remarks>
/// Not required to be a version 7 UUID: locally it comes from configuration, in the cloud
/// from the identity provider. It must only be non-empty.
/// </remarks>
public readonly record struct OwnerId : IStronglyTypedId<OwnerId>
{
    private OwnerId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static OwnerId From(Guid value) =>
        value == Guid.Empty ? throw new ArgumentException("An owner identifier cannot be empty.", nameof(value)) : new OwnerId(value);

    /// <summary>The identifier in the standard <c>D</c> format, as used in logs and routes.</summary>
    public override string ToString() => Value.ToString();
}
