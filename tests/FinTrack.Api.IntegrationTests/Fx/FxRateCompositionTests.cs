using FinTrack.Shared.Infrastructure.Fx;
using FinTrack.Shared.Kernel.Monetary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FinTrack.Api.IntegrationTests.Fx;

/// <summary>The Api registers the FX rate provider (plan 1a D8); its options default to Frankfurter with a 5 s limit.</summary>
public sealed class FxRateCompositionTests(ApiFixture fixture)
{
    [Fact]
    public async Task TheProvider_IsFrankfurter_WithTheDefaults()
    {
        await using var scope = fixture.Services.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IFxRateProvider>().ShouldBeOfType<FrankfurterFxRateProvider>();
        var options = fixture.Services.GetRequiredService<IOptions<FxRateOptions>>().Value;
        options.BaseAddress.ShouldBe(new Uri("https://api.frankfurter.dev/v1/"));
        options.Timeout.ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("FxRates:Timeout", "00:00:06")]
    [InlineData("FxRates:Timeout", "00:00:00")]
    [InlineData("FxRates:BaseAddress", "https://api.frankfurter.dev/v1")]
    public async Task InvalidOptions_FailAtStart(string key, string value)
    {
        await using var services = ApiFixture.BuildServices(fixture.ConnectionString, fixture.DatabaseName, more: new Dictionary<string, string?> { [key] = value });

        Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IOptions<FxRateOptions>>().Value);
    }
}
