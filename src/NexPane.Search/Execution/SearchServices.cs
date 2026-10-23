using NexPane.Core.Search;

namespace NexPane.Search.Execution;

/// <summary>Resolves <c>tag:</c> names to the tagged entries (implemented by <see cref="Tags.TagService"/>).</summary>
public interface ITagLookup
{
    Task<EntryKeySet> ResolveAsync(IReadOnlyList<string> tagNames, CancellationToken ct);
}

/// <summary>Resolves <c>category:Name</c> macros to the saved category AST (implemented by <see cref="Categories.SearchCatalog"/>).</summary>
public interface ICategoryResolver
{
    bool TryGetCategoryAst(string name, out QueryNode ast);
}

/// <summary>Configuration of the search engine (from <c>settings.json</c> section <c>search</c>).</summary>
public sealed record SearchEngineOptions
{
    /// <summary><c>type:</c> categories → folded extensions (without dot).</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> TypeMap { get; init; } = DefaultTypeMap();

    /// <summary>Maximum candidate files scanned by <c>content:/regex/</c> before a "cap reached" warning.</summary>
    public int ContentRegexMaxFiles { get; init; } = 5_000;

    /// <summary>Largest file read by the content regex scan when no extracted text exists.</summary>
    public long ContentRegexMaxBytesPerFile { get; init; } = 50L * 1024 * 1024;

    /// <summary>Maximum FTS5 hits used for <c>content:</c> terms.</summary>
    public int MaxFullTextHits { get; init; } = 100_000;

    /// <summary>Default top-k for <c>semantic:</c> (§8.2).</summary>
    public int SemanticTopK { get; init; } = 200;

    /// <summary>Entries per scan partition. §7.2 names 64K; 16K balances better across many cores (§21).</summary>
    public int PartitionSize { get; init; } = 1 << 14;

    public static IReadOnlyDictionary<string, IReadOnlySet<string>> DefaultTypeMap() =>
        Storage.Settings.SettingsDefaults.TypeMap.ToDictionary(
            p => p.Key,
            p => (IReadOnlySet<string>)new HashSet<string>(p.Value.Select(e => e.ToUpperInvariant()), StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Builds the options from the <c>search</c> settings section (falls back to defaults per value).</summary>
    public static SearchEngineOptions FromSettings(Storage.Settings.SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var section = settings.GetSection(Storage.Settings.SettingsDefaults.SearchSection) as System.Text.Json.Nodes.JsonObject;
        var options = new SearchEngineOptions();
        if (section is null)
        {
            return options;
        }

        if (section["typeMap"] is System.Text.Json.Nodes.JsonObject map)
        {
            var typeMap = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (category, list) in map)
            {
                if (list is System.Text.Json.Nodes.JsonArray array)
                {
                    typeMap[category] = new HashSet<string>(
                        array.Select(n => n?.GetValue<string>()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!.TrimStart('.').ToUpperInvariant()),
                        StringComparer.Ordinal);
                }
            }

            options = options with { TypeMap = typeMap };
        }

        if (section["contentRegexMaxFiles"]?.GetValue<int>() is int maxFiles and > 0)
        {
            options = options with { ContentRegexMaxFiles = maxFiles };
        }

        if (section["contentRegexMaxBytesPerFile"]?.GetValue<long>() is long maxBytes and > 0)
        {
            options = options with { ContentRegexMaxBytesPerFile = maxBytes };
        }

        return options;
    }
}
