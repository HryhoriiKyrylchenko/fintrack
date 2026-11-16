using FinTrack.Budget.Application;
using FinTrack.Budget.Domain.Settings;
using FinTrack.Shared.Infrastructure.Mongo;
using FinTrack.Shared.Kernel.Persistence;
using FinTrack.Shared.Kernel.Time;
using MongoDB.Driver;

namespace FinTrack.Budget.Infrastructure;

/// <summary>Reads Budget's documents through the owner-scoped collections, so every read is limited to the current owner.</summary>
internal sealed class MongoBudgetStore(IMongoDatabase database, IOwnerContext owner, IClock clock) : IBudgetStore
{
    public async Task<OwnerSettings?> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = new MutableCollection<OwnerSettings>(database, BudgetCollections.Settings, owner, clock);
        var found = await settings.FindAsync(FilterDefinition<OwnerSettings>.Empty, cancellationToken: cancellationToken).ConfigureAwait(false);

        // The unique index allows at most one per owner.
        return found.SingleOrDefault();
    }
}
