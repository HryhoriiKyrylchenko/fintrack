using FinTrack.Shared.Infrastructure.Owners;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FinTrack.Api.Middleware;

/// <summary>
/// Sets the owner of every HTTP invocation's scope. Local mode: the configured owner. The cloud
/// phase replaces this with the owner from the validated token (standard OIDC/JWT, provider-neutral).
/// Timer invocations have no owner; the outbox sweep sets one per message.
/// </summary>
internal sealed class OwnerMiddleware(IOptions<LocalOwnerOptions> options) : IFunctionsWorkerMiddleware
{
    private const string HttpTrigger = "httpTrigger";

    public Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var isHttp = context.FunctionDefinition.InputBindings.Values
            .Any(binding => string.Equals(binding.Type, HttpTrigger, StringComparison.OrdinalIgnoreCase));

        if (isHttp)
        {
            context.InstanceServices.GetRequiredService<ScopedOwnerContext>().Set(options.Value.OwnerId);
        }

        return next(context);
    }
}
