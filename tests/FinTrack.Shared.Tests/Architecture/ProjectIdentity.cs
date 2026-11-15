namespace FinTrack.Shared.Tests.Architecture;

/// <summary>The role a project plays in the architecture (CLAUDE.md, solution layout).</summary>
internal enum ProjectRole
{
    SharedKernel,
    SharedInfrastructure,
    Domain,
    Application,
    Infrastructure,
    Contracts,
    Api,
    Web,
}

/// <summary>
/// A source project identified by its name: <c>FinTrack.&lt;Context&gt;.&lt;Layer&gt;</c>,
/// <c>FinTrack.Shared.Kernel</c>, <c>FinTrack.Shared.Infrastructure</c>, <c>FinTrack.Api</c> or <c>FinTrack.Web</c>.
/// </summary>
internal sealed record ProjectIdentity(string Name, string Context, ProjectRole Role)
{
    private const string Prefix = "FinTrack";
    private const string SharedContext = "Shared";
    private const string HostContext = "Host";

    /// <summary>Parses a project name; returns <c>null</c> when the name does not fit the layout.</summary>
    public static ProjectIdentity? TryParse(string name)
    {
        var parts = name.Split('.');
        if (parts.Length is < 2 or > 3 || parts[0] != Prefix)
        {
            return null;
        }

        if (parts.Length == 2)
        {
            return parts[1] switch
            {
                "Api" => new ProjectIdentity(name, HostContext, ProjectRole.Api),
                "Web" => new ProjectIdentity(name, HostContext, ProjectRole.Web),
                _ => null,
            };
        }

        var context = parts[1];
        var layer = parts[2];

        if (context == SharedContext)
        {
            return layer switch
            {
                "Kernel" => new ProjectIdentity(name, context, ProjectRole.SharedKernel),
                "Infrastructure" => new ProjectIdentity(name, context, ProjectRole.SharedInfrastructure),
                _ => null,
            };
        }

        if (context is "Api" or "Web")
        {
            return null;
        }

        return layer switch
        {
            "Domain" => new ProjectIdentity(name, context, ProjectRole.Domain),
            "Application" => new ProjectIdentity(name, context, ProjectRole.Application),
            "Infrastructure" => new ProjectIdentity(name, context, ProjectRole.Infrastructure),
            "Contracts" => new ProjectIdentity(name, context, ProjectRole.Contracts),
            _ => null,
        };
    }
}
