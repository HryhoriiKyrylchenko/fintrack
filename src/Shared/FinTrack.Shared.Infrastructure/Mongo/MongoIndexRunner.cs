using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.Mongo;

/// <summary>
/// Runs every registered <see cref="IMongoIndexInitializer"/> once per process. Concurrent callers
/// share the same run; after a failure the next call tries again.
/// </summary>
public sealed class MongoIndexRunner
{
    private readonly IMongoDatabase database;
    private readonly IMongoIndexInitializer[] initializers;
    private readonly Lock gate = new();
    private Task? run;

    /// <summary>Creates the runner for <paramref name="initializers"/>.</summary>
    public MongoIndexRunner(IMongoDatabase database, IEnumerable<IMongoIndexInitializer> initializers)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(initializers);

        this.database = database;
        this.initializers = [.. initializers];
    }

    /// <summary>Completes when all indexes exist. Cancelling stops waiting, not the shared run.</summary>
    public Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (run is null || run.IsFaulted || run.IsCanceled)
            {
                run = RunAsync();
            }

            return run.WaitAsync(cancellationToken);
        }
    }

    private async Task RunAsync()
    {
        foreach (var initializer in initializers)
        {
            await initializer.CreateIndexesAsync(database, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
