namespace FinTrack.Shared.Tests.Architecture;

/// <summary>
/// Architecture rules 3 and 4 of CLAUDE.md, written as data so they can be audited in one place.
/// Changing a rule here is an architecture decision: record it in docs/ARCHITECTURE.md as well.
/// </summary>
internal static class ArchitectureRules
{
    /// <summary>Packages that must never be referenced anywhere, with the reason.</summary>
    public static readonly IReadOnlyDictionary<string, string> ForbiddenPackages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["MediatR"] = "commercially licensed from version 13; use Mediator",
        ["FluentAssertions"] = "paid licence from version 8; use Shouldly",
        ["Microsoft.Azure.Cosmos"] = "storage is MongoDB only",
    };

    /// <summary>
    /// Packages each role may reference directly. An entry ending in <c>*</c> is a prefix.
    /// Anything not listed is rejected, so a new package needs a deliberate entry here.
    /// </summary>
    private static readonly IReadOnlyDictionary<ProjectRole, string[]> AllowedPackages = new Dictionary<ProjectRole, string[]>
    {
        [ProjectRole.SharedKernel] = ["FluentResults"],
        [ProjectRole.SharedInfrastructure] = ["FluentResults", "FluentValidation", "Mediator.Abstractions", "MongoDB.Driver", "Microsoft.Extensions.*"],
        [ProjectRole.Domain] = ["FluentResults"],
        [ProjectRole.Contracts] = [],
        [ProjectRole.Application] = ["FluentResults", "FluentValidation", "Mediator.Abstractions", "Microsoft.Extensions.Logging.Abstractions"],
        [ProjectRole.Infrastructure] = ["MongoDB.Driver", "Microsoft.Extensions.*"],
        [ProjectRole.Api] = ["FluentValidation", "Mediator.Abstractions", "Mediator.SourceGenerator", "Microsoft.Azure.Functions.Worker*", "Microsoft.Extensions.*"],
        [ProjectRole.Web] = ["MudBlazor", "Microsoft.AspNetCore.Components.WebAssembly*", "Microsoft.Extensions.*"],
    };

    /// <summary>Whether <paramref name="from"/> may reference the project <paramref name="to"/>.</summary>
    public static bool IsProjectReferenceAllowed(ProjectIdentity from, ProjectIdentity to)
    {
        var sameContext = from.Context == to.Context;

        return (from.Role, to.Role) switch
        {
            // The kernel references nothing; everything else may use the kernel.
            (ProjectRole.SharedKernel, _) => false,
            (_, ProjectRole.SharedKernel) => true,

            // Shared.Infrastructure (MongoDB, mediator pipeline) is used only by infrastructure and the host.
            (ProjectRole.SharedInfrastructure, _) => false,
            (ProjectRole.Infrastructure or ProjectRole.Api, ProjectRole.SharedInfrastructure) => true,

            // The Function App composes contexts: Application, Infrastructure and Contracts, never Domain directly.
            (ProjectRole.Api, ProjectRole.Application or ProjectRole.Infrastructure or ProjectRole.Contracts) => true,

            // The front end sees only read contracts.
            (ProjectRole.Web, ProjectRole.Contracts) => true,

            // Contracts are the only door between contexts (rule 3).
            (ProjectRole.Application or ProjectRole.Infrastructure, ProjectRole.Contracts) => true,

            // Dependencies point inward within one context (rule 4).
            (ProjectRole.Application, ProjectRole.Domain) => sameContext,
            (ProjectRole.Infrastructure, ProjectRole.Domain or ProjectRole.Application) => sameContext,

            _ => false,
        };
    }

    /// <summary>Whether a project with <paramref name="role"/> may reference <paramref name="package"/> directly.</summary>
    public static bool IsPackageAllowed(ProjectRole role, string package) =>
        AllowedPackages[role].Any(allowed => allowed.EndsWith('*')
            ? package.StartsWith(allowed[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(allowed, package, StringComparison.OrdinalIgnoreCase));
}
