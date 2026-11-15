using System.Xml.Linq;

namespace FinTrack.Shared.Tests.Architecture;

/// <summary>Locates the repository and reads its project files.</summary>
internal static class Repository
{
    private const string SolutionFileName = "FinTrack.sln";

    public static DirectoryInfo Root { get; } = FindRoot();

    public static string SolutionFile => Path.Combine(Root.FullName, SolutionFileName);

    /// <summary>All project files below <paramref name="folder"/> (relative to the root), excluding build output.</summary>
    public static IReadOnlyList<ProjectFile> ProjectsIn(string folder)
    {
        var directory = Path.Combine(Root.FullName, folder);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .Order(StringComparer.Ordinal)
            .Select(ProjectFile.Load)
            .ToList();
    }

    /// <summary>MSBuild files at the root that may declare packages for every project.</summary>
    public static IReadOnlyList<string> RootBuildFiles() =>
        ["Directory.Build.props", "Directory.Packages.props", Path.Combine("tests", "Directory.Build.props")];

    private static bool IsBuildOutput(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("bin") || segments.Contains("obj");
    }

    private static DirectoryInfo FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
        {
            directory = directory.Parent;
        }

        return directory ?? throw new InvalidOperationException($"{SolutionFileName} was not found above {AppContext.BaseDirectory}.");
    }
}

/// <summary>The references declared in one project file.</summary>
internal sealed record ProjectFile(string FullPath, string Name, IReadOnlyList<string> ProjectReferences, IReadOnlyList<string> PackageReferences)
{
    public string RelativePath => Path.GetRelativePath(Repository.Root.FullName, FullPath);

    public static ProjectFile Load(string fullPath)
    {
        var document = XDocument.Load(fullPath);

        return new ProjectFile(
            fullPath,
            Path.GetFileNameWithoutExtension(fullPath),
            [.. IncludeValues(document, "ProjectReference").Select(ProjectNameFromPath)],
            [.. IncludeValues(document, "PackageReference")]);
    }

    /// <summary>The <c>Include</c> (or <c>Update</c>) values of all elements named <paramref name="elementName"/>.</summary>
    public static IEnumerable<string> IncludeValues(XDocument document, string elementName) =>
        document.Descendants(elementName)
            .Select(element => (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update"))
            .OfType<string>();

    // Project paths may use either separator; Linux does not treat '\' as one.
    private static string ProjectNameFromPath(string includePath) =>
        Path.GetFileNameWithoutExtension(includePath.Replace('\\', '/'));
}
