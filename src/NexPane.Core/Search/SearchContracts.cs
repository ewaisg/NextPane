using NexPane.Core.Indexing;
using NexPane.Core.Vfs;

namespace NexPane.Core.Search;

public enum SortColumn
{
    /// <summary>Index order (cheapest; the UI may sort lazily).</summary>
    None,
    Name,
    Path,
    Size,
    Modified,
    Created,
    Extension,
}

public sealed record SortSpec(SortColumn Column = SortColumn.None, bool Descending = false)
{
    public static SortSpec Unsorted { get; } = new();

    /// <summary>Parses the persisted form <c>column:asc|desc</c> (e.g. <c>name:asc</c>, §9.2 <c>sort_spec</c>).</summary>
    public static SortSpec Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Unsorted;
        }

        var parts = text.Split(':', 2);
        var column = Enum.TryParse<SortColumn>(parts[0], ignoreCase: true, out var c) ? c : SortColumn.None;
        var descending = parts.Length > 1 && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase);
        return new SortSpec(column, descending);
    }

    public override string ToString() => Column.ToString().ToLowerInvariant() + (Descending ? ":desc" : ":asc");
}

public sealed record SearchRequest(
    QueryNode Query,
    IReadOnlyList<VfsPath> Scope,
    SortSpec Sort,
    int? Limit = null,
    SearchRequest? RefinementOf = null);

public sealed record SearchResult(
    IReadOnlyList<EntryRef> Items,
    long Generation,
    TimeSpan Elapsed,
    bool Truncated,
    IReadOnlyList<string> Warnings)
{
    public static SearchResult Empty(long generation) => new([], generation, TimeSpan.Zero, false, []);
}

/// <summary>Change of a live result set (§6.3): entries that started or stopped matching, or a full replacement.</summary>
public sealed record ResultDelta(
    long Generation,
    IReadOnlyList<EntryRef> Added,
    IReadOnlyList<EntryRef> Removed,
    IReadOnlyList<EntryRef> Changed,
    bool Reset,
    SearchResult? Snapshot = null);

public interface ILiveQuery : IDisposable
{
    SearchResult Current { get; }

    IAsyncEnumerable<ResultDelta> Deltas(CancellationToken ct);
}

public interface ISearchEngine
{
    Task<SearchResult> ExecuteAsync(SearchRequest request, CancellationToken ct);

    /// <summary>Live results; dispose to unsubscribe.</summary>
    ILiveQuery Subscribe(SearchRequest request);
}

/// <summary>Embedding similarity search (implemented by the AI module, §9.3). Returns full paths with scores.</summary>
public interface ISemanticSearch
{
    bool IsAvailable { get; }

    Task<IReadOnlyList<(string Path, double Score)>> SearchAsync(string text, int topK, CancellationToken ct);
}
