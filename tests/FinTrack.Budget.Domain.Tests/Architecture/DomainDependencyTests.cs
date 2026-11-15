using FinTrack.Tests.Common;

namespace FinTrack.Budget.Domain.Tests.Architecture;

/// <summary>
/// The Budget domain has no references to infrastructure, MongoDB, Functions or the
/// mediator library, and does not know its own outer layers (CLAUDE.md rule 4).
/// </summary>
public class DomainDependencyTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "MongoDB.",
        "Mediator",
        "Microsoft.Azure.Functions",
        "Microsoft.AspNetCore",
        "MudBlazor",
        "FinTrack.Shared.Infrastructure",
        "FinTrack.Budget.Application",
        "FinTrack.Budget.Infrastructure",
        "FinTrack.Budget.Contracts",
    ];

    [Fact]
    public void DomainAssembly_DoesNotReferenceInfrastructureOrFrameworks() =>
        AssemblyGraph.ForbiddenReferences(typeof(AssemblyMarker).Assembly, ForbiddenPrefixes).ShouldBeEmpty();
}
