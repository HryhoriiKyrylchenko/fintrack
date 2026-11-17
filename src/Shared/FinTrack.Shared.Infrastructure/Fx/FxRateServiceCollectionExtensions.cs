using FinTrack.Shared.Infrastructure.Mongo;
using FinTrack.Shared.Kernel.Monetary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.Fx;

/// <summary>Registers <see cref="IFxRateProvider"/>.</summary>
public static class FxRateServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="FrankfurterFxRateProvider"/> as <see cref="IFxRateProvider"/>, its stored rates
    /// in <c>ref_fx_rates</c> with their index, and its HTTP client. Needs
    /// <see cref="MongoServiceCollectionExtensions.AddFinTrackMongo"/> and an <c>IClock</c>.
    /// </summary>
    /// <remarks>
    /// The HTTP client comes from <c>IHttpClientFactory</c>, which reuses connections across requests
    /// (a new <c>HttpClient</c> per request would exhaust sockets, an <c>HttpClient</c> kept for the
    /// whole process would miss DNS changes). Invalid options fail at start.
    /// </remarks>
    public static IServiceCollection AddFxRates(this IServiceCollection services, Action<FxRateOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<FxRateOptions>()
            .Configure(configure ?? (_ => { }))
            .Validate(
                options => options.BaseAddress is { IsAbsoluteUri: true } address && address.AbsoluteUri.EndsWith('/'),
                $"{FxRateOptions.SectionName}:{nameof(FxRateOptions.BaseAddress)} must be an absolute address ending with '/'.")
            .Validate(
                options => options.Timeout > TimeSpan.Zero && options.Timeout <= FxRateOptions.MaxTimeout,
                $"{FxRateOptions.SectionName}:{nameof(FxRateOptions.Timeout)} must be above zero and at most {FxRateOptions.MaxTimeout.TotalSeconds} seconds.")
            .ValidateOnStart();

        services.AddMongoIndexInitializer<FxRateIndexInitializer>();
        services.TryAddSingleton(provider => new ReferenceCollection<FxRateSet>(provider.GetRequiredService<IMongoDatabase>(), FxRateSet.CollectionName));

        services.AddHttpClient<IFxRateProvider, FrankfurterFxRateProvider>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<FxRateOptions>>().Value;
            client.BaseAddress = options.BaseAddress;
            client.Timeout = options.Timeout;
        });

        return services;
    }
}
