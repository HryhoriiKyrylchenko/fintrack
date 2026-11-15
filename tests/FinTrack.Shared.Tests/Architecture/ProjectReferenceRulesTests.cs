using System.Xml.Linq;

namespace FinTrack.Shared.Tests.Architecture;

/// <summary>
/// Enforces CLAUDE.md rules 3 (context isolation) and 4 (dependencies point inward)
/// on the declared references of every project file.
/// </summary>
public class ProjectReferenceRulesTests
{
    private static readonly IReadOnlyList<ProjectFile> SourceProjects = Repository.ProjectsIn("src");
    private static readonly IReadOnlyList<ProjectFile> TestProjects = Repository.ProjectsIn("tests");

    [Fact]
    public void SourceProjects_Exist() => SourceProjects.ShouldNotBeEmpty();

    [Fact]
    public void SourceProjects_FollowNamingLayout()
    {
        var violations = SourceProjects
            .Where(project => ProjectIdentity.TryParse(project.Name) is null)
            .Select(project => $"{project.RelativePath}: name does not fit the layout in CLAUDE.md")
            .ToList();

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void ProjectReferences_FollowDependencyRules()
    {
        var violations = new List<string>();

        foreach (var project in SourceProjects)
        {
            if (ProjectIdentity.TryParse(project.Name) is not { } from)
            {
                continue;
            }

            foreach (var referenceName in project.ProjectReferences)
            {
                if (ProjectIdentity.TryParse(referenceName) is not { } to)
                {
                    violations.Add($"{project.Name} -> {referenceName}: referenced project does not fit the layout");
                }
                else if (!ArchitectureRules.IsProjectReferenceAllowed(from, to))
                {
                    violations.Add($"{project.Name} ({from.Role}) -> {to.Name} ({to.Role}) is not allowed");
                }
            }
        }

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void PackageReferences_AreAllowedForTheirLayer()
    {
        var violations = new List<string>();

        foreach (var project in SourceProjects)
        {
            if (ProjectIdentity.TryParse(project.Name) is not { } identity)
            {
                continue;
            }

            violations.AddRange(project.PackageReferences
                .Where(package => !ArchitectureRules.IsPackageAllowed(identity.Role, package))
                .Select(package => $"{project.Name} ({identity.Role}) -> package {package} is not on the allow-list"));
        }

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void ForbiddenPackages_AreNotReferencedAnywhere()
    {
        var declarations = SourceProjects.Concat(TestProjects)
            .SelectMany(project => project.PackageReferences.Select(package => (File: project.RelativePath, Package: package)))
            .Concat(Repository.RootBuildFiles()
                .Select(file => Path.Combine(Repository.Root.FullName, file))
                .Where(File.Exists)
                .SelectMany(path => PackagesDeclaredIn(path).Select(package => (File: Path.GetRelativePath(Repository.Root.FullName, path), Package: package))));

        var violations = declarations
            .Where(declaration => ArchitectureRules.ForbiddenPackages.ContainsKey(declaration.Package))
            .Select(declaration => $"{declaration.File}: {declaration.Package} is forbidden ({ArchitectureRules.ForbiddenPackages[declaration.Package]})")
            .ToList();

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void AllProjects_AreInTheSolution()
    {
        var solution = File.ReadAllText(Repository.SolutionFile);

        var missing = SourceProjects.Concat(TestProjects)
            .Where(project => !solution.Contains($"{project.Name}.csproj", StringComparison.Ordinal))
            .Select(project => project.RelativePath)
            .ToList();

        missing.ShouldBeEmpty();
    }

    private static IEnumerable<string> PackagesDeclaredIn(string path)
    {
        var document = XDocument.Load(path);
        return ProjectFile.IncludeValues(document, "PackageReference")
            .Concat(ProjectFile.IncludeValues(document, "PackageVersion"));
    }
}
