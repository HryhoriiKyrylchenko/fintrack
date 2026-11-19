using System.Globalization;
using FinTrack.Web;
using FinTrack.Web.Api;
using FinTrack.Web.Buckets;
using FinTrack.Web.Settings;
using FinTrack.Web.Theme;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The Api's address comes from wwwroot/appsettings.json, so a deployment changes configuration, not code.
var apiBaseUrl = builder.Configuration["Api:BaseUrl"]
    ?? throw new InvalidOperationException("Api:BaseUrl is missing in wwwroot/appsettings.json.");

// The runtime starts with the browser's time zone and language; read them before the owner's locale replaces the culture.
builder.Services.AddSingleton(new BrowserDefaults(TimeZoneInfo.Local.Id, CultureInfo.CurrentCulture.Name));

// Singletons: in WebAssembly there is one user and one scope anyway, and the settings are loaded before the app starts.
builder.Services.AddSingleton(_ => new HttpClient { BaseAddress = new Uri(apiBaseUrl) });
builder.Services.AddSingleton<ApiClient>();
builder.Services.AddSingleton<BucketsClient>();
builder.Services.AddSingleton<OwnerSettingsState>();
builder.Services.AddScoped<ThemePreference>();
builder.Services.AddLocalization();
builder.Services.AddMudServices();

var host = builder.Build();

// The owner's locale formats numbers and dates, so it must be in place before the first render
// (plan 1a D7). Without saved settings the layout shows the first-run screen; on failure, an error with retry.
var settings = host.Services.GetRequiredService<OwnerSettingsState>();
await settings.LoadAsync(CancellationToken.None).ConfigureAwait(false);
if (settings.Settings is { } saved)
{
    OwnerCulture.TryApply(saved.Locale);
}

await host.RunAsync().ConfigureAwait(false);
