using FinTrack.Api.Health;
using FinTrack.Api.Http;
using FinTrack.Api.Outbox;
using FinTrack.Shared.Infrastructure.Health;
using FinTrack.Shared.Infrastructure.Owners;
using FinTrack.Shared.Kernel.Ledger;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FinTrack.Api.IntegrationTests.Functions;

/// <summary>The functions, called the way the worker calls them, with the Api's own container.</summary>
public sealed class FunctionTests(ApiFixture fixture)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetHealth_WithMongoDbRunning_IsOk()
    {
        await using var scope = fixture.Services.CreateAsyncScope();

        var result = await ActivatorUtilities.CreateInstance<GetHealthFunction>(scope.ServiceProvider)
            .Run(new DefaultHttpContext().Request, Cancellation);

        var report = result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<HealthReport>();
        report.Status.ShouldBe(GetHealthHandler.Healthy);
    }

    [Fact]
    public async Task GetHealth_WithMongoDbUnreachable_IsAProblemWithStatus503()
    {
        // Nothing listens on port 1; the short selection timeout stands in for a stopped server.
        await using var services = ApiFixture.BuildServices("mongodb://localhost:1/?serverSelectionTimeoutMS=500", "fintrack_unreachable");
        await using var scope = services.CreateAsyncScope();

        var result = await ActivatorUtilities.CreateInstance<GetHealthFunction>(scope.ServiceProvider)
            .Run(new DefaultHttpContext().Request, Cancellation);

        var problem = result.ShouldBeOfType<ObjectResult>();
        problem.StatusCode.ShouldBe(503);
        problem.Value.ShouldBeOfType<ProblemDetails>().Extensions["code"].ShouldBe(GetHealthHandler.DatabaseUnavailable);
    }

    [Fact]
    public async Task SweepOutbox_RunsAgainstTheDatabase()
    {
        await using var scope = fixture.Services.CreateAsyncScope();

        await Should.NotThrowAsync(() => ActivatorUtilities.CreateInstance<SweepOutboxFunction>(scope.ServiceProvider)
            .Run(new TimerInfo(), Cancellation));
    }

    [Fact]
    public void Composition_ResolvesTheLedgerForTheConfiguredOwner()
    {
        using var scope = fixture.Services.CreateScope();
        var owner = scope.ServiceProvider.GetRequiredService<IOptions<LocalOwnerOptions>>().Value.OwnerId;
        scope.ServiceProvider.GetRequiredService<ScopedOwnerContext>().Set(owner);

        scope.ServiceProvider.GetRequiredService<ILedger>().ShouldNotBeNull();
        owner.Value.ShouldBe(Guid.Parse(ApiFixture.LocalOwnerId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Composition_RejectsAMissingLocalOwner(string localOwnerId)
    {
        using var services = ApiFixture.BuildServices(fixture.ConnectionString, "fintrack_unused", localOwnerId);

        Should.Throw<Exception>(() => services.GetRequiredService<IOptions<LocalOwnerOptions>>().Value);
    }

    [Fact]
    public void UnexpectedErrorResponse_IsAProblemWithStatus500() =>
        ProblemDetailsMapper.UnexpectedError().Status.ShouldBe(500);
}
