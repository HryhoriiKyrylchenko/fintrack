using FinTrack.Shared.Infrastructure.Ledger;
using FinTrack.Shared.Infrastructure.Messaging;
using FinTrack.Shared.Infrastructure.Mongo;
using FinTrack.Shared.Kernel.Time;
using Mediator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FinTrack.Api;

/// <summary>
/// Everything the Function App registers. <c>Program.cs</c> calls it, and the integration tests
/// build exactly the same container from it.
/// </summary>
public static class ApiComposition
{
    /// <summary>Registers MongoDB, the clock, the ledger and outbox, the local owner and the mediator.</summary>
    public static IServiceCollection AddFinTrackApi(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IClock, SystemClock>();
        services.AddFinTrackMongo(options => configuration.GetSection(MongoOptions.SectionName).Bind(options));
        services.AddMongoLedger();

        services.AddOptions<LocalOwnerOptions>()
            .Bind(configuration.GetSection(LocalOwnerOptions.SectionName))
            .Validate(options => options.LocalOwnerId != Guid.Empty, $"{LocalOwnerOptions.SectionName}:{nameof(LocalOwnerOptions.LocalOwnerId)} must be a non-empty GUID.")
            .ValidateOnStart();

        // Handlers are scoped because they use the request's owner and ledger. Validation runs
        // before retry: invalid input is never retried.
        services.AddMediator((MediatorOptions options) =>
        {
            options.Namespace = "FinTrack.Api.Mediator";
            options.ServiceLifetime = ServiceLifetime.Scoped;
            options.PipelineBehaviors = [typeof(ValidationBehavior<,>), typeof(RetryOnConflictBehavior<,>)];
        });

        return services;
    }
}
