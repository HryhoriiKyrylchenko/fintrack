using FinTrack.Api.Health;
using FinTrack.Shared.Infrastructure.Mongo;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;

namespace FinTrack.Api.Middleware;

/// <summary>
/// Makes sure every context's collections and indexes exist before the first function of this
/// process uses them (once per process; <see cref="MongoIndexRunner"/>). The health check is
/// exempt, so it can still report an unreachable database instead of failing here.
/// </summary>
internal sealed class IndexMiddleware(MongoIndexRunner runner) : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        if (context.FunctionDefinition.Name != GetHealthFunction.Name)
        {
            await runner.EnsureCreatedAsync(context.CancellationToken).ConfigureAwait(false);
        }

        await next(context).ConfigureAwait(false);
    }
}
