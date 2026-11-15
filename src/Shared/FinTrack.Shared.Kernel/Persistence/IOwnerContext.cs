using FinTrack.Shared.Kernel.Ids;

namespace FinTrack.Shared.Kernel.Persistence;

/// <summary>
/// The single source of the current owner. Stores filter every query and write by it;
/// code never passes an owner around as a parameter, so it cannot pass the wrong one.
/// </summary>
/// <remarks>
/// Locally the owner is a fixed value from configuration. In the cloud it comes from the
/// validated token. The outbox sweep sets it per message, to that message's owner.
/// </remarks>
public interface IOwnerContext
{
    /// <summary>The owner of the current request or message.</summary>
    OwnerId OwnerId { get; }
}
