using System.Collections.Immutable;
using FinTrack.Api.Health;
using FinTrack.Api.Middleware;
using FinTrack.Shared.Infrastructure.Mongo;
using FinTrack.Shared.Infrastructure.Owners;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Persistence;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace FinTrack.Api.IntegrationTests.Middleware;

/// <summary>The worker middlewares, run with a hand-written function context.</summary>
public sealed class MiddlewareTests(ApiFixture fixture)
{
    [Fact]
    public async Task OwnerMiddleware_SetsTheLocalOwnerForHttpFunctions()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var context = new FakeFunctionContext("AnyHttpFunction", "httpTrigger", scope.ServiceProvider);
        OwnerId? seen = null;

        await Owner().Invoke(context, invoked =>
        {
            seen = invoked.InstanceServices.GetRequiredService<IOwnerContext>().OwnerId;
            return Task.CompletedTask;
        });

        seen.ShouldBe(OwnerId.From(Guid.Parse(ApiFixture.LocalOwnerId)));
    }

    [Fact]
    public async Task OwnerMiddleware_LeavesTimerFunctionsWithoutAnOwner()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var context = new FakeFunctionContext("SweepOutbox", "timerTrigger", scope.ServiceProvider);

        await Owner().Invoke(context, _ => Task.CompletedTask);

        Should.Throw<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<ScopedOwnerContext>().OwnerId);
    }

    [Fact]
    public async Task IndexMiddleware_CreatesIndexesBeforeOtherFunctions_ButNotBeforeTheHealthCheck()
    {
        var initializer = new CountingInitializer();
        var middleware = new IndexMiddleware(new MongoIndexRunner(fixture.Services.GetRequiredService<IMongoDatabase>(), [initializer]));
        await using var scope = fixture.Services.CreateAsyncScope();

        await middleware.Invoke(new FakeFunctionContext(GetHealthFunction.Name, "httpTrigger", scope.ServiceProvider), _ => Task.CompletedTask);
        initializer.Runs.ShouldBe(0);

        await middleware.Invoke(new FakeFunctionContext("SweepOutbox", "timerTrigger", scope.ServiceProvider), _ => Task.CompletedTask);
        await middleware.Invoke(new FakeFunctionContext("AnyHttpFunction", "httpTrigger", scope.ServiceProvider), _ => Task.CompletedTask);
        initializer.Runs.ShouldBe(1);
    }

    private OwnerMiddleware Owner() => new(fixture.Services.GetRequiredService<IOptions<LocalOwnerOptions>>());

    private sealed class CountingInitializer : IMongoIndexInitializer
    {
        public int Runs { get; private set; }

        public Task CreateIndexesAsync(IMongoDatabase database, CancellationToken cancellationToken)
        {
            Runs++;
            return Task.CompletedTask;
        }
    }
}

/// <summary>Just enough of a <see cref="FunctionContext"/> for middleware: a name, one trigger and the scope's services.</summary>
internal sealed class FakeFunctionContext(string functionName, string triggerType, IServiceProvider services) : FunctionContext
{
    public override string InvocationId => "invocation-1";

    public override string FunctionId => functionName;

    public override TraceContext TraceContext => throw new NotSupportedException();

    public override BindingContext BindingContext => throw new NotSupportedException();

    public override RetryContext RetryContext => throw new NotSupportedException();

    public override IServiceProvider InstanceServices { get; set; } = services;

    public override FunctionDefinition FunctionDefinition { get; } = new FakeFunctionDefinition(functionName, triggerType);

    public override IDictionary<object, object> Items { get; set; } = new Dictionary<object, object>();

    public override IInvocationFeatures Features => throw new NotSupportedException();

    private sealed class FakeFunctionDefinition(string name, string triggerType) : FunctionDefinition
    {
        public override ImmutableArray<FunctionParameter> Parameters => [];

        public override string PathToAssembly => string.Empty;

        public override string EntryPoint => name;

        public override string Id => name;

        public override string Name => name;

        public override IImmutableDictionary<string, BindingMetadata> InputBindings { get; } =
            ImmutableDictionary<string, BindingMetadata>.Empty.Add("trigger", new FakeBinding(triggerType));

        public override IImmutableDictionary<string, BindingMetadata> OutputBindings => ImmutableDictionary<string, BindingMetadata>.Empty;
    }

    private sealed class FakeBinding(string type) : BindingMetadata
    {
        public override string Name => "trigger";

        public override string Type => type;

        public override BindingDirection Direction => BindingDirection.In;
    }
}
