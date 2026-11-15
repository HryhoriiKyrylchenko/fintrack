using System.Reflection;

namespace FinTrack.Tests.Common;

/// <summary>
/// Walks the compiled references of an assembly. Used by the architecture tests to
/// prove that a layer does not depend on frameworks it must not know about.
/// </summary>
/// <remarks>
/// The compiler records only assemblies that the code actually uses, so this check
/// complements the project-file rules in FinTrack.Shared.Tests: those catch a forbidden
/// declaration, this catches forbidden use, including use through another FinTrack project.
/// </remarks>
internal static class AssemblyGraph
{
    private const string OwnAssemblyPrefix = "FinTrack.";

    /// <summary>
    /// Returns the names of all assemblies referenced by <paramref name="root"/>,
    /// following references through FinTrack assemblies transitively.
    /// </summary>
    public static IReadOnlyCollection<string> ReferencedAssemblyNames(Assembly root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<Assembly>();
        pending.Enqueue(root);

        while (pending.Count > 0)
        {
            foreach (var reference in pending.Dequeue().GetReferencedAssemblies())
            {
                if (reference.Name is null || !seen.Add(reference.Name))
                {
                    continue;
                }

                if (reference.Name.StartsWith(OwnAssemblyPrefix, StringComparison.Ordinal))
                {
                    pending.Enqueue(Assembly.Load(reference));
                }
            }
        }

        return seen;
    }

    /// <summary>
    /// Returns the referenced assemblies of <paramref name="root"/> whose names start with any of
    /// <paramref name="forbiddenPrefixes"/>.
    /// </summary>
    public static IReadOnlyList<string> ForbiddenReferences(Assembly root, IEnumerable<string> forbiddenPrefixes)
    {
        var prefixes = forbiddenPrefixes.ToList();

        return ReferencedAssemblyNames(root)
            .Where(name => prefixes.Exists(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
