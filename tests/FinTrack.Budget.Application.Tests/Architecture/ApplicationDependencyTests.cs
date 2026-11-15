using FinTrack.Tests.Common;

namespace FinTrack.Budget.Application.Tests.Architecture;

/// <summary>
/// The Budget application layer may use the mediator library and FluentValidation, but not
/// MongoDB, Functions or any infrastructure project (CLAUDE.md rule 4).
/// </summary>
public class ApplicationDependencyTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "MongoDB.",
        "Microsoft.Azure.Functions",
        "Microsoft.AspNetCore",
        "MudBlazor",
        "FinTrack.Shared.Infrastructure",
        "FinTrack.Budget.Infrastructure",
    ];

    [Fact]
    public void ApplicationAssembly_DoesNotReferenceInfrastructureOrFrameworks() =>
        AssemblyGraph.ForbiddenReferences(typeof(AssemblyMarker).Assembly, ForbiddenPrefixes).ShouldBeEmpty();
}
