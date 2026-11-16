namespace FinTrack.Shared.Infrastructure.Mongo;

/// <summary>Where the application's MongoDB database is.</summary>
/// <remarks>
/// The connection string may contain a password: it comes from user secrets, environment
/// variables or Key Vault, never from a committed file, and it is never logged.
/// </remarks>
public sealed class MongoOptions
{
    /// <summary>The configuration section these options are read from.</summary>
    public const string SectionName = "Mongo";

    /// <summary>
    /// A MongoDB connection string. Locally a single-node replica set, for example
    /// <c>mongodb://localhost:27017/?replicaSet=rs0</c>; transactions need a replica set.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>The database that holds every context's collections, for example <c>fintrack</c>.</summary>
    public string DatabaseName { get; set; } = string.Empty;
}
