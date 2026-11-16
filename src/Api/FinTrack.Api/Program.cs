using FinTrack.Api;
using FinTrack.Api.Middleware;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Order matters: errors outermost, then the owner, then the indexes, then the function.
builder.UseMiddleware<ExceptionMiddleware>();
builder.UseMiddleware<OwnerMiddleware>();
builder.UseMiddleware<IndexMiddleware>();

builder.Services.AddFinTrackApi(builder.Configuration);

await builder.Build().RunAsync().ConfigureAwait(false);
