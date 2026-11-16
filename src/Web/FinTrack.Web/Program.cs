using FinTrack.Web;
using FinTrack.Web.Api;
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

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(apiBaseUrl) });
builder.Services.AddScoped<ApiClient>();
builder.Services.AddScoped<ThemePreference>();
builder.Services.AddLocalization();
builder.Services.AddMudServices();

await builder.Build().RunAsync().ConfigureAwait(false);
