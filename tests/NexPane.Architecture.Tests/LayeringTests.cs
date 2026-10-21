namespace NexPane.Architecture.Tests;

/// <summary>Enforces the module boundaries and dependency direction in ARCHITECTURE.md §4.</summary>
public sealed class LayeringTests
{
    public static TheoryData<string> SourceProjectNames()
    {
        var data = new TheoryData<string>();
        foreach (var p in RepositoryModel.SourceProjects)
        {
            data.Add(p.Name);
        }

        return data;
    }

    private static ProjectInfo Source(string name) => RepositoryModel.SourceProjects.Single(p => p.Name == name);

    [Fact]
    public void Every_layer_project_exists_under_src()
    {
        RepositoryModel.SourceProjects.Select(p => p.Name)
            .Should().BeEquivalentTo(LayeringRules.AllKnownSourceProjects,
                "the set of src projects must match the layer table in ARCHITECTURE.md §3/§4 exactly");
    }

    [Theory]
    [MemberData(nameof(SourceProjectNames))]
    public void Project_references_only_allowed_layers(string project)
    {
        var allowed = LayeringRules.AllowedReferences(project);
        // NotContain(!allowed) instead of OnlyContain(allowed): FluentAssertions 7 fails OnlyContain on empty collections.
        Source(project).ProjectReferences.Should().NotContain(r => !allowed.Contains(r),
            $"{project} may only reference [{string.Join(", ", allowed)}]");
    }

    [Fact]
    public void Feature_modules_never_reference_each_other()
    {
        foreach (var feature in LayeringRules.L2)
        {
            Source(feature).ProjectReferences.Should().NotContain(r => LayeringRules.L2.Contains(r),
                $"{feature} is an L2 feature module; cross-feature collaboration must go through interfaces in NexPane.Core");
        }
    }

    [Fact]
    public void Core_references_nothing_and_targets_plain_net10()
    {
        var core = Source(LayeringRules.Core);
        core.ProjectReferences.Should().BeEmpty();
        core.PackageReferences.Should().BeEmpty("the domain layer has no third-party dependencies");
        core.TargetFramework.Should().Be("net10.0", "NexPane.Core must stay free of Windows-specific APIs");
        core.UsesWpf.Should().BeFalse();
        core.UsesWindowsForms.Should().BeFalse();
    }

    [Fact]
    public void Only_presentation_projects_use_wpf_and_none_use_windows_forms()
    {
        foreach (var p in RepositoryModel.SourceProjects)
        {
            p.UsesWindowsForms.Should().BeFalse($"{p.Name} must not use Windows Forms");
            if (!LayeringRules.L3.Contains(p.Name))
            {
                p.UsesWpf.Should().BeFalse($"{p.Name} is below the presentation layer and must not reference WPF (Shell takes HWNDs as nint)");
            }
        }
    }

    [Fact]
    public void Source_projects_have_no_raw_assembly_references()
    {
        foreach (var p in RepositoryModel.SourceProjects)
        {
            p.AssemblyReferences.Should().BeEmpty($"{p.Name} must use ProjectReference/PackageReference only, so layering stays checkable");
        }
    }

    [Fact]
    public void Nothing_references_the_app_or_the_index_service()
    {
        foreach (var p in RepositoryModel.SourceProjects)
        {
            p.ProjectReferences.Should().NotContain([LayeringRules.App, LayeringRules.IndexService],
                $"{p.Name}: App and IndexService are composition roots / hosts");
        }
    }

    [Fact]
    public void Source_projects_never_reference_test_projects()
    {
        var testNames = RepositoryModel.TestProjects.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var p in RepositoryModel.SourceProjects)
        {
            p.ProjectReferences.Should().NotContain(r => testNames.Contains(r), $"{p.Name} must not depend on tests");
        }
    }

    [Fact]
    public void Source_dependency_graph_is_acyclic()
    {
        var graph = RepositoryModel.SourceProjects.ToDictionary(p => p.Name, p => p.ProjectReferences, StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = visiting, 2 = done

        void Visit(string node, Stack<string> path)
        {
            if (state.TryGetValue(node, out var s))
            {
                s.Should().NotBe(1, $"cycle detected: {string.Join(" -> ", path.Reverse())} -> {node}");
                return;
            }

            state[node] = 1;
            path.Push(node);
            foreach (var next in graph.GetValueOrDefault(node) ?? [])
            {
                Visit(next, path);
            }

            path.Pop();
            state[node] = 2;
        }

        foreach (var node in graph.Keys)
        {
            Visit(node, new Stack<string>());
        }
    }
}
