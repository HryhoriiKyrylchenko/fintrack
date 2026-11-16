using FinTrack.Api.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace FinTrack.Api.Middleware;

/// <summary>
/// The last line of defence: an exception (a bug or an infrastructure fault, CLAUDE.md rule 12)
/// in an HTTP function becomes a 500 <c>ProblemDetails</c> with code <c>server.error</c> and no
/// details, so internals never reach the client. The exception is logged; its message never
/// contains amounts by convention. Other triggers rethrow, so the host records the failure.
/// </summary>
internal sealed partial class ExceptionMiddleware(ILogger<ExceptionMiddleware> logger) : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogUnhandled(exception, context.FunctionDefinition.Name, context.InvocationId);

            var http = context.GetHttpContext();
            if (http is null || http.Response.HasStarted)
            {
                throw;
            }

            var problem = ProblemDetailsMapper.UnexpectedError();
            http.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await http.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", context.CancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Function {FunctionName} failed (invocation {InvocationId}).")]
    private partial void LogUnhandled(Exception exception, string functionName, string invocationId);
}
