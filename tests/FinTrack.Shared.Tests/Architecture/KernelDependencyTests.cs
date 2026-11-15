using FinTrack.Shared.Kernel;
using FinTrack.Tests.Common;

namespace FinTrack.Shared.Tests.Architecture;

/// <summary>
/// The kernel is referenced by every Domain project, so it must stay free of
/// infrastructure and frameworks (CLAUDE.md rule 4).
/// </summary>
public class KernelDependencyTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "MongoDB.",
        "Mediator",
        "FluentValidation",
        "Microsoft.Azure.Functions",
        "Microsoft.AspNetCore",
        "MudBlazor",
        "FinTrack.Shared.Infrastructure",
        "FinTrack.Budget.",
    ];

    [Fact]
    public void KernelAssembly_DoesNotReferenceInfrastructureOrFrameworks() =>
        AssemblyGraph.ForbiddenReferences(typeof(AssemblyMarker).Assembly, ForbiddenPrefixes).ShouldBeEmpty();
}
