namespace NexPane.Architecture.Tests;

/// <summary>The layer model from ARCHITECTURE.md §4, expressed as an allow-list of project references.</summary>
internal static class LayeringRules
{
    internal const string Core = "NexPane.Core";
    internal const string Shell = "NexPane.Shell";
    internal const string Storage = "NexPane.Storage";
    internal const string Preview = "NexPane.Preview";
    internal const string App = "NexPane.App";
    internal const string IndexService = "NexPane.IndexService";
    internal const string Indexing = "NexPane.Indexing";

    /// <summary>L0 domain.</summary>
    internal static readonly string[] L0 = [Core];

    /// <summary>L1 platform.</summary>
    internal static readonly string[] L1 = [Shell, Storage];

    /// <summary>L2 feature modules. They must never reference each other.</summary>
    internal static readonly string[] L2 =
    [
        "NexPane.Security", Indexing, "NexPane.Search", "NexPane.Archives", "NexPane.Network",
        "NexPane.Sync", "NexPane.Metadata", "NexPane.Scripting", "NexPane.AI",
    ];

    /// <summary>L3 presentation.</summary>
    internal static readonly string[] L3 = [Preview, App];

    /// <summary>Hosts.</summary>
    internal static readonly string[] Hosts = [IndexService];

    internal static IEnumerable<string> AllKnownSourceProjects => L0.Concat(L1).Concat(L2).Concat(L3).Concat(Hosts);

    /// <summary>Allowed direct project references per source project.</summary>
    internal static IReadOnlySet<string> AllowedReferences(string project)
    {
        if (L0.Contains(project))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        if (L1.Contains(project))
        {
            return new HashSet<string>([Core], StringComparer.Ordinal);
        }

        if (L2.Contains(project))
        {
            return new HashSet<string>([Core, Shell, Storage], StringComparer.Ordinal);
        }

        return project switch
        {
            Preview => new HashSet<string>([Core, Shell], StringComparer.Ordinal),
            App => new HashSet<string>(L0.Concat(L1).Concat(L2).Append(Preview), StringComparer.Ordinal),
            IndexService => new HashSet<string>([Core, Shell, Storage, Indexing], StringComparer.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(project), project,
                "Unclassified source project. Add it to a layer in ARCHITECTURE.md §4 and in LayeringRules."),
        };
    }
}
