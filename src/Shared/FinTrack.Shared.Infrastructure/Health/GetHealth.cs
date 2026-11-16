using FinTrack.Shared.Kernel.Results;
using FinTrack.Shared.Kernel.Time;
using FluentResults;
using Mediator;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.Health;

/// <summary>Is the application able to serve requests? Checks that MongoDB answers.</summary>
public sealed record GetHealth : IQuery<Result<HealthReport>>;

/// <summary>The answer of a healthy application.</summary>
/// <param name="Status">Always <c>healthy</c>; an unhealthy application answers with an error instead.</param>
/// <param name="CheckedAt">When the check ran.</param>
public sealed record HealthReport(string Status, DateTimeOffset CheckedAt);

/// <summary>
/// Pings MongoDB. When it does not answer within <see cref="Timeout"/>, the result fails with
/// <c>Unavailable</c> <see cref="DatabaseUnavailable"/> (HTTP 503) instead of throwing, because for a
/// health check an unreachable database is an expected answer, not a bug.
/// </summary>
public sealed partial class GetHealthHandler(IMongoDatabase database, IClock clock, ILogger<GetHealthHandler> logger)
    : IQueryHandler<GetHealth, Result<HealthReport>>
{
    /// <summary>The error code when MongoDB does not answer.</summary>
    public const string DatabaseUnavailable = "health.database_unavailable";

    /// <summary>The healthy status.</summary>
    public const string Healthy = "healthy";

    /// <summary>How long the check waits for MongoDB.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public async ValueTask<Result<HealthReport>> Handle(GetHealth query, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: timeout.Token).ConfigureAwait(false);
            return Result.Ok(new HealthReport(Healthy, clock.UtcNow));
        }
        catch (Exception exception) when (exception is TimeoutException or MongoException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogDatabaseUnavailable(exception.GetType().Name);
            return Result.Fail<HealthReport>(AppError.Unavailable(DatabaseUnavailable));
        }
    }

    // Only the exception type: driver messages contain the server address.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Health check: MongoDB did not answer ({ExceptionType}).")]
    private partial void LogDatabaseUnavailable(string exceptionType);
}
