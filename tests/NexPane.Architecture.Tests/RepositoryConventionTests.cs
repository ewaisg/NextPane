using System.Xml.Linq;

namespace NexPane.Architecture.Tests;

/// <summary>Repository-wide conventions from ARCHITECTURE.md §2/§15 and HANDOFF.md.</summary>
public sealed class RepositoryConventionTests
{
    [Fact]
    public void Every_project_is_included_in_the_solution()
    {
        var sln = File.ReadAllText(Path.Combine(RepositoryModel.Root, "NexPane.sln"));
        var all = RepositoryModel.Load("src").Concat(RepositoryModel.Load("tests")).Concat(RepositoryModel.Load("tools"));
        foreach (var p in all)
        {
            sln.Should().Contain($"\"{p.Name}\"", $"{p.Name}.csproj must be added to NexPane.sln");
        }
    }

    [Fact]
    public void Package_versions_are_managed_centrally()
    {
        var all = RepositoryModel.Load("src").Concat(RepositoryModel.Load("tests")).Concat(RepositoryModel.Load("tools"));
        foreach (var p in all)
        {
            p.PackageReferences.Should().NotContain(r => r.Version != null,
                $"{p.Name}: versions belong in Directory.Packages.props (central package management)");
        }
    }

    [Fact]
    public void FluentAssertions_is_pinned_to_the_last_apache_licensed_version()
    {
        var props = XDocument.Load(Path.Combine(RepositoryModel.Root, "Directory.Packages.props"));
        var fa = props.Descendants("PackageVersion").Single(e => (string?)e.Attribute("Include") == "FluentAssertions");
        ((string?)fa.Attribute("Version")).Should().Be("[7.2.0]", "FluentAssertions 8+ requires a commercial license");
    }

    [Fact]
    public void Shared_build_settings_lock_the_stack()
    {
        var props = XDocument.Load(Path.Combine(RepositoryModel.Root, "Directory.Build.props"));
        string? Prop(string name) => props.Descendants(name).Select(e => e.Value).FirstOrDefault();

        Prop("TargetFramework").Should().Be("net10.0-windows10.0.19041.0");
        Prop("SupportedOSPlatformVersion").Should().Be("10.0.17763.0");
        Prop("PlatformTarget").Should().Be("x64");
        Prop("Nullable").Should().Be("enable");
        Prop("TreatWarningsAsErrors").Should().Be("true");
    }

    [Fact]
    public void Global_json_pins_the_net10_sdk_band()
    {
        var json = File.ReadAllText(Path.Combine(RepositoryModel.Root, "global.json"));
        json.Should().Contain("\"10.0.").And.Contain("latestFeature");
    }

    [Fact]
    public void Executables_are_never_trimmed_or_single_file()
    {
        foreach (var name in new[] { LayeringRules.App, LayeringRules.IndexService })
        {
            var doc = XDocument.Load(RepositoryModel.SourceProjects.Single(p => p.Name == name).FullPath);
            doc.Descendants("PublishTrimmed").Should().BeEmpty($"{name}: WPF and COM interop are not trim-safe");
            doc.Descendants("PublishSingleFile").Should().BeEmpty($"{name}: native libraries require a folder publish");
        }
    }
}
