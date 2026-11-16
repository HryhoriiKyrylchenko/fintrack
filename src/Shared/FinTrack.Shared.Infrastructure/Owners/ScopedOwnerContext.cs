using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Persistence;

namespace FinTrack.Shared.Infrastructure.Owners;

/// <summary>
/// The <see cref="IOwnerContext"/> of one DI scope: set once when the scope starts, by the HTTP
/// request handling (locally the configured owner, in the cloud the token's owner) or by the outbox
/// dispatcher (the message's owner). Reading it before it is set is a bug.
/// </summary>
public sealed class ScopedOwnerContext : IOwnerContext
{
    private OwnerId? owner;

    /// <inheritdoc />
    public OwnerId OwnerId => owner ?? throw new InvalidOperationException("The owner of this scope has not been set.");

    /// <summary>Sets the owner; setting the same owner again is allowed, another owner throws.</summary>
    public void Set(OwnerId value)
    {
        if (owner is { } current && current != value)
        {
            throw new InvalidOperationException("The owner of this scope is already set to another owner.");
        }

        owner = value;
    }
}
