using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.Mongo;

/// <summary>
/// Checks <see cref="MongoOptions"/> when they are first used. Failure messages never contain
/// the connection string, because it may hold a password.
/// </summary>
internal sealed class MongoOptionsValidator : IValidateOptions<MongoOptions>
{
    // MongoDB rejects these characters in database names (on Windows the strictest set applies).
    private const string ForbiddenDatabaseNameCharacters = "/\\. \"$*<>:|?";
    private const int MaxDatabaseNameLength = 63;

    public ValidateOptionsResult Validate(string? name, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            failures.Add($"{nameof(MongoOptions.ConnectionString)} is required.");
        }
        else if (!IsValidConnectionString(options.ConnectionString))
        {
            failures.Add($"{nameof(MongoOptions.ConnectionString)} is not a valid MongoDB connection string.");
        }

        if (!IsValidDatabaseName(options.DatabaseName))
        {
            failures.Add($"{nameof(MongoOptions.DatabaseName)} must have 1 to {MaxDatabaseNameLength} characters and none of: {ForbiddenDatabaseNameCharacters}");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsValidConnectionString(string connectionString)
    {
        try
        {
            _ = MongoUrl.Create(connectionString);
            return true;
        }
        catch (MongoConfigurationException)
        {
            return false;
        }
    }

    private static bool IsValidDatabaseName(string? databaseName) =>
        databaseName is { Length: > 0 and <= MaxDatabaseNameLength }
        && databaseName.IndexOfAny(ForbiddenDatabaseNameCharacters.ToCharArray()) < 0
        && !databaseName.Any(char.IsControl);
}
