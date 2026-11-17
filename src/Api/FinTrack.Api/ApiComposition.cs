using FinTrack.Api.Middleware;
using FinTrack.Budget.Infrastructure;
using FinTrack.Shared.Infrastructure.Fx;
using FinTrack.Shared.Infrastructure.Ledger;
using FinTrack.Shared.Infrastructure.Messaging;
using FinTrack.Shared.Infrastructure.Mongo;
using FinTrack.Shared.Kernel.Json;
using FinTrack.Shared.Kernel.Time;
using Mediator;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FinTrack.Api;

/// <summary>
/// Everything the Function App registers. <c>Program.cs</c> calls it, and the integration tests
/// build exactly the same container (or the same host) from it.
/// </summary>
public static class ApiComposition
{
    /// <summary>
    /// The whole Function App: the ASP.NET Core integration for HTTP, the worker middleware and
    /// <see cref="AddFinTrackApi"/>. <c>Program.cs</c> only builds and runs the result.
    /// </summary>
    public static FunctionsApplicationBuilder ConfigureFinTrackApi(this FunctionsApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureFunctionsWebApplication();

        // Order matters: errors outermost, then the owner, then the indexes, then the function.
        builder.UseMiddleware<ExceptionMiddleware>();
        builder.UseMiddleware<OwnerMiddleware>();
        builder.UseMiddleware<IndexMiddleware>();

        builder.Services.AddFinTrackApi(builder.Configuration);
        return builder;
    }

    /// <summary>Registers the JSON contract, MongoDB, the clock, the ledger and outbox, FX rates, the Budget context, the local owner and the mediator.</summary>
    public static IServiceCollection AddFinTrackApi(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // IActionResult values are written by MVC's output formatter (the ASP.NET Core integration
        // calls AddMvc and ExecuteResultAsync), so the contract goes into MVC's JSON options.
        services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(options => FinTrackJson.Configure(options.JsonSerializerOptions));

        services.AddSingleton<IClock, SystemClock>();
        services.AddFinTrackMongo(options => configuration.GetSection(MongoOptions.SectionName).Bind(options));
        services.AddMongoLedger();
        services.AddFxRates(options => configuration.GetSection(FxRateOptions.SectionName).Bind(options));
        services.AddBudget();

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
