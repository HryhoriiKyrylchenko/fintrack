using FinTrack.Shared.Kernel.Ids;

namespace FinTrack.Api;

/// <summary>
/// Local mode has one user and no login (ARCHITECTURE 2.4): every request is handled for this
/// owner. In the cloud phase the owner comes from the validated token instead.
/// </summary>
public sealed class LocalOwnerOptions
{
    /// <summary>The configuration section, for example <c>FinTrack__LocalOwnerId</c> in app settings.</summary>
    public const string SectionName = "FinTrack";

    /// <summary>The local owner; any non-empty GUID, chosen once and kept.</summary>
    public Guid LocalOwnerId { get; set; }

    /// <summary>The owner as a typed identifier.</summary>
    public OwnerId OwnerId => OwnerId.From(LocalOwnerId);
}
