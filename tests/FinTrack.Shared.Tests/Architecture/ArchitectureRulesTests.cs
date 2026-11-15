namespace FinTrack.Shared.Tests.Architecture;

/// <summary>
/// Tests the rule table itself, including projects that do not exist yet
/// (Api, Web, a second context), so the rules are checked before they are needed.
/// </summary>
public class ArchitectureRulesTests
{
    [Theory]
    [InlineData("FinTrack.Shared.Kernel", true)]
    [InlineData("FinTrack.Shared.Infrastructure", true)]
    [InlineData("FinTrack.Budget.Domain", true)]
    [InlineData("FinTrack.Investments.Contracts", true)]
    [InlineData("FinTrack.Api", true)]
    [InlineData("FinTrack.Web", true)]
    [InlineData("FinTrack.Shared", false)]
    [InlineData("FinTrack.Budget.Core", false)]
    [InlineData("FinTrack.Budget.Domain.Extra", false)]
    [InlineData("FinTrack.Api.Domain", false)]
    [InlineData("Other.Budget.Domain", false)]
    public void TryParse_RecognisesLayoutNames(string name, bool expected) =>
        (ProjectIdentity.TryParse(name) is not null).ShouldBe(expected);

    [Theory]
    // Kernel and Shared.Infrastructure.
    [InlineData("FinTrack.Shared.Kernel", "FinTrack.Shared.Infrastructure", false)]
    [InlineData("FinTrack.Shared.Infrastructure", "FinTrack.Shared.Kernel", true)]
    [InlineData("FinTrack.Shared.Infrastructure", "FinTrack.Budget.Contracts", false)]
    // Inside one context: dependencies point inward.
    [InlineData("FinTrack.Budget.Domain", "FinTrack.Shared.Kernel", true)]
    [InlineData("FinTrack.Budget.Domain", "FinTrack.Shared.Infrastructure", false)]
    [InlineData("FinTrack.Budget.Domain", "FinTrack.Budget.Application", false)]
    [InlineData("FinTrack.Budget.Domain", "FinTrack.Budget.Contracts", false)]
    [InlineData("FinTrack.Budget.Contracts", "FinTrack.Budget.Domain", false)]
    [InlineData("FinTrack.Budget.Application", "FinTrack.Budget.Domain", true)]
    [InlineData("FinTrack.Budget.Application", "FinTrack.Budget.Contracts", true)]
    [InlineData("FinTrack.Budget.Application", "FinTrack.Budget.Infrastructure", false)]
    [InlineData("FinTrack.Budget.Application", "FinTrack.Shared.Infrastructure", false)]
    [InlineData("FinTrack.Budget.Infrastructure", "FinTrack.Budget.Application", true)]
    [InlineData("FinTrack.Budget.Infrastructure", "FinTrack.Shared.Infrastructure", true)]
    // Across contexts: Contracts only.
    [InlineData("FinTrack.Investments.Application", "FinTrack.Budget.Contracts", true)]
    [InlineData("FinTrack.Investments.Infrastructure", "FinTrack.Budget.Contracts", true)]
    [InlineData("FinTrack.Investments.Application", "FinTrack.Budget.Domain", false)]
    [InlineData("FinTrack.Investments.Infrastructure", "FinTrack.Budget.Infrastructure", false)]
    [InlineData("FinTrack.Investments.Domain", "FinTrack.Budget.Contracts", false)]
    // Hosts.
    [InlineData("FinTrack.Api", "FinTrack.Budget.Application", true)]
    [InlineData("FinTrack.Api", "FinTrack.Budget.Infrastructure", true)]
    [InlineData("FinTrack.Api", "FinTrack.Shared.Infrastructure", true)]
    [InlineData("FinTrack.Api", "FinTrack.Budget.Domain", false)]
    [InlineData("FinTrack.Api", "FinTrack.Web", false)]
    [InlineData("FinTrack.Web", "FinTrack.Budget.Contracts", true)]
    [InlineData("FinTrack.Web", "FinTrack.Shared.Kernel", true)]
    [InlineData("FinTrack.Web", "FinTrack.Budget.Application", false)]
    [InlineData("FinTrack.Web", "FinTrack.Shared.Infrastructure", false)]
    [InlineData("FinTrack.Budget.Infrastructure", "FinTrack.Api", false)]
    public void IsProjectReferenceAllowed_FollowsRules(string from, string to, bool expected) =>
        ArchitectureRules.IsProjectReferenceAllowed(Parse(from), Parse(to)).ShouldBe(expected);

    [Theory]
    [InlineData("SharedKernel", "FluentResults", true)]
    [InlineData("SharedKernel", "MongoDB.Driver", false)]
    [InlineData("Domain", "Mediator.Abstractions", false)]
    [InlineData("Domain", "FluentValidation", false)]
    [InlineData("Application", "Mediator.Abstractions", true)]
    [InlineData("Application", "MongoDB.Driver", false)]
    [InlineData("Infrastructure", "MongoDB.Driver", true)]
    [InlineData("Infrastructure", "Microsoft.Extensions.Options", true)]
    [InlineData("Api", "Microsoft.Azure.Functions.Worker.Extensions.Timer", true)]
    [InlineData("Api", "MongoDB.Driver", false)]
    [InlineData("Web", "MudBlazor", true)]
    [InlineData("Web", "MongoDB.Driver", false)]
    [InlineData("Contracts", "FluentResults", false)]
    public void IsPackageAllowed_FollowsAllowList(string role, string package, bool expected) =>
        ArchitectureRules.IsPackageAllowed(Enum.Parse<ProjectRole>(role), package).ShouldBe(expected);

    private static ProjectIdentity Parse(string name) =>
        ProjectIdentity.TryParse(name) ?? throw new ArgumentException($"{name} does not fit the layout.", nameof(name));
}
