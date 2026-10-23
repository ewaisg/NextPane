using System.Xml.Linq;

namespace NexPane.Architecture.Tests;

/// <summary>A parsed .csproj file: name, layer-relevant properties and NexPane project references.</summary>
internal sealed record ProjectInfo(
    string Name,
    string FullPath,
    string? TargetFramework,
    bool UsesWpf,
    bool UsesWindowsForms,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> AssemblyReferences,
    IReadOnlyList<(string Package, string? Version)> PackageReferences,
    IReadOnlyList<string> FrameworkReferences);

/// <summary>Discovers the repository root and parses every project under src/, tests/ and tools/.</summary>
internal static class RepositoryModel
{
    private static readonly Lazy<string> RootLazy = new(FindRoot);

    internal static string Root => RootLazy.Value;

    internal static IReadOnlyList<ProjectInfo> SourceProjects => Load("src");

    internal static IReadOnlyList<ProjectInfo> TestProjects => Load("tests");

    internal static IReadOnlyList<ProjectInfo> Load(string folder)
    {
        var dir = Path.Combine(Root, folder);
        if (!Directory.Exists(dir))
        {
            return [];
        }

        return Directory.EnumerateFiles(dir, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(Parse)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
    }

    internal static ProjectInfo Parse(string csprojPath)
    {
        var doc = XDocument.Load(csprojPath);
        string? Prop(string name) => doc.Descendants(name).Select(e => e.Value.Trim()).LastOrDefault();

        var projectRefs = doc.Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Select(i => Path.GetFileNameWithoutExtension(i!.Replace('\\', Path.DirectorySeparatorChar)))
            .ToList();

        var assemblyRefs = doc.Descendants("Reference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            .ToList();

        var packages = doc.Descendants("PackageReference")
            .Select(e => ((string?)e.Attribute("Include") ?? string.Empty, (string?)e.Attribute("Version")))
            .ToList();

        var frameworkRefs = doc.Descendants("FrameworkReference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            .ToList();

        return new ProjectInfo(
            Path.GetFileNameWithoutExtension(csprojPath),
            csprojPath,
            Prop("TargetFramework"),
            string.Equals(Prop("UseWPF"), "true", StringComparison.OrdinalIgnoreCase),
            string.Equals(Prop("UseWindowsForms"), "true", StringComparison.OrdinalIgnoreCase),
            projectRefs,
            assemblyRefs,
            packages,
            frameworkRefs);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NexPane.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException(
            $"NexPane.sln not found above '{AppContext.BaseDirectory}'. Architecture tests must run from the repository build output.");
    }
}
