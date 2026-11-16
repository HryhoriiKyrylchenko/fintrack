using FinTrack.Api;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFinTrackApi();

await builder.Build().RunAsync().ConfigureAwait(false);
