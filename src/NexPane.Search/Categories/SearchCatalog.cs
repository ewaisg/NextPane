using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexPane.Core.Search;
using NexPane.Core.Vfs;
using NexPane.Search.Execution;
using NexPane.Search.Language;
using NexPane.Storage;

namespace NexPane.Search.Categories;

/// <summary>One column of a saved column layout.</summary>
public sealed record ColumnSetting(string Id, double Width, bool Visible = true);

public sealed record ColumnLayout(long LayoutId, string Name, IReadOnlyList<ColumnSetting> Columns);

/// <summary>Ordered refinement chain: each AST narrows the previous result (§9.2 <c>saved_filters</c>).</summary>
public sealed record SavedFilter(long FilterId, string Name, IReadOnlyList<QueryNode> Chain, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc)
{
    /// <summary>The chain as one AST (AND of all steps).</summary>
    public QueryNode? Combined => Chain.Count switch
    {
        0 => null,
        1 => Chain[0],
        _ => new AndNode(new NodeList(Chain)),
    };
}

/// <summary>A user-defined search category (§9.2 <c>search_categories</c>), usable as <c>category:Name</c>.</summary>
public sealed record SearchCategory(
    long CategoryId,
    string Name,
    string? Icon,
    QueryNode Query,
    string QueryText,
    long? FilterId,
    long? LayoutId,
    SortSpec Sort,
    IReadOnlyList<VfsPath> Scope,
    int SortOrder,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

/// <summary>Input for creating or updating a category.</summary>
public sealed record SearchCategoryDefinition(
    string Name,
    QueryNode Query,
    string? Icon = null,
    long? FilterId = null,
    long? LayoutId = null,
    SortSpec? Sort = null,
    IReadOnlyList<VfsPath>? Scope = null,
    int SortOrder = 0);

/// <summary>Thrown when a category definition is invalid (e.g. recursive <c>category:</c> references).</summary>
public sealed class CategoryValidationException : Exception
{
    public CategoryValidationException()
    {
    }

    public CategoryValidationException(string message)
        : base(message)
    {
    }

    public CategoryValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Persistence of saved filter chains, search categories and column layouts in app.db (P3.28), plus the
/// <c>category:</c> macro resolver. Categories are cached in memory and the cache is refreshed after every change.
/// Saving a category that (directly or through other categories) refers to itself is rejected.
/// </summary>
public sealed class SearchCatalog : ICategoryResolver
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SqliteConnectionFactory _appDb;
    private readonly SqliteWriter _writer;
    private readonly TimeProvider _clock;
    private readonly QueryPrinter _printer = new();
    private readonly Lock _gate = new();
    private Dictionary<string, SearchCategory> _categories = new(StringComparer.OrdinalIgnoreCase);

    public SearchCatalog(SqliteConnectionFactory appDb, SqliteWriter writer, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(appDb);
        ArgumentNullException.ThrowIfNull(writer);
        _appDb = appDb;
        _writer = writer;
        _clock = clock ?? TimeProvider.System;
    }

    public event EventHandler? CategoriesChanged;

    public bool TryGetCategoryAst(string name, out QueryNode ast)
    {
        lock (_gate)
        {
            if (_categories.TryGetValue(name, out var category))
            {
                ast = category.Query;
                return true;
            }
        }

        ast = null!;
        return false;
    }

    /// <summary>Loads all categories into the resolver cache.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        var categories = await ListCategoriesAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            _categories = categories.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        }
    }

    // ================================================================ categories

    public async Task<IReadOnlyList<SearchCategory>> ListCategoriesAsync(CancellationToken ct) =>
        await QueryAsync(
            "SELECT category_id, name, icon, query_text, ast_json, filter_id, layout_id, sort_spec, scope_json, sort_order, created_utc, updated_utc FROM search_categories ORDER BY sort_order, name;",
            null,
            ReadCategory,
            ct).ConfigureAwait(false);

    public async Task<SearchCategory?> GetCategoryAsync(string name, CancellationToken ct) =>
        (await QueryAsync(
            "SELECT category_id, name, icon, query_text, ast_json, filter_id, layout_id, sort_spec, scope_json, sort_order, created_utc, updated_utc FROM search_categories WHERE name = $n;",
            cmd => cmd.Parameters.AddWithValue("$n", name),
            ReadCategory,
            ct).ConfigureAwait(false)).FirstOrDefault();

    /// <summary>Creates or replaces a category by name. Throws <see cref="CategoryValidationException"/> for recursion
    /// or unknown referenced categories.</summary>
    public async Task<SearchCategory> SaveCategoryAsync(SearchCategoryDefinition definition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
        Validate(definition.Name.Trim(), definition.Query);
        var now = _clock.GetUtcNow().ToFileTime();
        var text = _printer.Print(definition.Query);
        var astJson = JsonSerializer.Serialize(definition.Query, Json);
        var scopeJson = JsonSerializer.Serialize((definition.Scope ?? []).Select(s => s.ToString()), Json);
        await _writer.ExecuteAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO search_categories(name, icon, query_text, ast_json, filter_id, layout_id, sort_spec, scope_json, sort_order, created_utc, updated_utc)
                VALUES ($name, $icon, $text, $ast, $filter, $layout, $sort, $scope, $order, $now, $now)
                ON CONFLICT(name) DO UPDATE SET icon = excluded.icon, query_text = excluded.query_text, ast_json = excluded.ast_json,
                    filter_id = excluded.filter_id, layout_id = excluded.layout_id, sort_spec = excluded.sort_spec,
                    scope_json = excluded.scope_json, sort_order = excluded.sort_order, updated_utc = excluded.updated_utc;
                """;
            cmd.Parameters.AddWithValue("$name", definition.Name.Trim());
            cmd.Parameters.AddWithValue("$icon", (object?)definition.Icon ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$text", text);
            cmd.Parameters.AddWithValue("$ast", astJson);
            cmd.Parameters.AddWithValue("$filter", (object?)definition.FilterId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$layout", (object?)definition.LayoutId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sort", (definition.Sort ?? new SortSpec(SortColumn.Name)).ToString());
            cmd.Parameters.AddWithValue("$scope", scopeJson);
            cmd.Parameters.AddWithValue("$order", definition.SortOrder);
            cmd.Parameters.AddWithValue("$now", now);
            return cmd.ExecuteNonQuery();
        }, ct).ConfigureAwait(false);

        await LoadAsync(ct).ConfigureAwait(false);
        CategoriesChanged?.Invoke(this, EventArgs.Empty);
        return (await GetCategoryAsync(definition.Name.Trim(), ct).ConfigureAwait(false))!;
    }

    public async Task<bool> DeleteCategoryAsync(string name, CancellationToken ct)
    {
        var deleted = await ExecuteAsync("DELETE FROM search_categories WHERE name = $n;", cmd => cmd.Parameters.AddWithValue("$n", name), ct).ConfigureAwait(false);
        await LoadAsync(ct).ConfigureAwait(false);
        CategoriesChanged?.Invoke(this, EventArgs.Empty);
        return deleted > 0;
    }

    /// <summary>Checks that saving <paramref name="name"/> with <paramref name="query"/> creates no category cycle and
    /// references only existing categories.</summary>
    public void Validate(string name, QueryNode query)
    {
        ArgumentNullException.ThrowIfNull(query);
        Dictionary<string, QueryNode> graph;
        lock (_gate)
        {
            graph = _categories.ToDictionary(p => p.Key, p => p.Value.Query, StringComparer.OrdinalIgnoreCase);
        }

        graph[name] = query;
        var visiting = new List<string>();
        Visit(name);

        void Visit(string current)
        {
            if (visiting.Contains(current, StringComparer.OrdinalIgnoreCase))
            {
                throw new CategoryValidationException(
                    $"Category '{name}' would refer to itself ({string.Join(" → ", visiting.Append(current))}).");
            }

            if (!graph.TryGetValue(current, out var ast))
            {
                throw new CategoryValidationException($"Category '{visiting.LastOrDefault() ?? name}' refers to unknown category '{current}'.");
            }

            visiting.Add(current);
            foreach (var reference in References(ast))
            {
                Visit(reference);
            }

            visiting.RemoveAt(visiting.Count - 1);
        }
    }

    private static IEnumerable<string> References(QueryNode node) => node switch
    {
        AndNode a => a.Children.SelectMany(References),
        OrNode o => o.Children.SelectMany(References),
        NotNode n => References(n.Operand),
        TermNode { Field: QueryField.Category, Value: TextValue t } => [t.Text],
        _ => [],
    };

    // ================================================================ saved filters

    public async Task<SavedFilter> SaveFilterAsync(string name, IReadOnlyList<QueryNode> chain, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(chain);
        var now = _clock.GetUtcNow().ToFileTime();
        var json = JsonSerializer.Serialize(chain, Json);
        await _writer.ExecuteAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO saved_filters(name, chain_json, created_utc, updated_utc) VALUES ($n, $j, $now, $now)
                ON CONFLICT(name) DO UPDATE SET chain_json = excluded.chain_json, updated_utc = excluded.updated_utc;
                """;
            cmd.Parameters.AddWithValue("$n", name.Trim());
            cmd.Parameters.AddWithValue("$j", json);
            cmd.Parameters.AddWithValue("$now", now);
            return cmd.ExecuteNonQuery();
        }, ct).ConfigureAwait(false);
        return (await GetFilterAsync(name.Trim(), ct).ConfigureAwait(false))!;
    }

    public async Task<SavedFilter?> GetFilterAsync(string name, CancellationToken ct) =>
        (await QueryAsync("SELECT filter_id, name, chain_json, created_utc, updated_utc FROM saved_filters WHERE name = $n;",
            cmd => cmd.Parameters.AddWithValue("$n", name), ReadFilter, ct).ConfigureAwait(false)).FirstOrDefault();

    public async Task<IReadOnlyList<SavedFilter>> ListFiltersAsync(CancellationToken ct) =>
        await QueryAsync("SELECT filter_id, name, chain_json, created_utc, updated_utc FROM saved_filters ORDER BY name;", null, ReadFilter, ct).ConfigureAwait(false);

    public async Task<bool> DeleteFilterAsync(string name, CancellationToken ct) =>
        await ExecuteAsync("DELETE FROM saved_filters WHERE name = $n;", cmd => cmd.Parameters.AddWithValue("$n", name), ct).ConfigureAwait(false) > 0;

    // ================================================================ column layouts

    public async Task<ColumnLayout> SaveLayoutAsync(string name, IReadOnlyList<ColumnSetting> columns, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);
        var json = JsonSerializer.Serialize(columns, Json);
        await ExecuteAsync("""
            INSERT INTO column_layouts(name, columns_json) VALUES ($n, $j) ON CONFLICT(name) DO UPDATE SET columns_json = excluded.columns_json;
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("$n", name.Trim());
            cmd.Parameters.AddWithValue("$j", json);
        }, ct).ConfigureAwait(false);
        return (await GetLayoutAsync(name.Trim(), ct).ConfigureAwait(false))!;
    }

    public async Task<ColumnLayout?> GetLayoutAsync(string name, CancellationToken ct) =>
        (await QueryAsync("SELECT layout_id, name, columns_json FROM column_layouts WHERE name = $n;",
            cmd => cmd.Parameters.AddWithValue("$n", name), ReadLayout, ct).ConfigureAwait(false)).FirstOrDefault();

    public async Task<IReadOnlyList<ColumnLayout>> ListLayoutsAsync(CancellationToken ct) =>
        await QueryAsync("SELECT layout_id, name, columns_json FROM column_layouts ORDER BY name;", null, ReadLayout, ct).ConfigureAwait(false);

    public async Task<bool> DeleteLayoutAsync(string name, CancellationToken ct) =>
        await ExecuteAsync("DELETE FROM column_layouts WHERE name = $n;", cmd => cmd.Parameters.AddWithValue("$n", name), ct).ConfigureAwait(false) > 0;

    // ================================================================ helpers

    private static SearchCategory ReadCategory(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.IsDBNull(2) ? null : r.GetString(2),
        JsonSerializer.Deserialize<QueryNode>(r.GetString(4), Json)!,
        r.GetString(3),
        r.IsDBNull(5) ? null : r.GetInt64(5),
        r.IsDBNull(6) ? null : r.GetInt64(6),
        SortSpec.Parse(r.GetString(7)),
        (JsonSerializer.Deserialize<List<string>>(r.GetString(8), Json) ?? []).Select(VfsPath.Parse).ToList(),
        r.GetInt32(9),
        DateTimeOffset.FromFileTime(r.GetInt64(10)).ToUniversalTime(),
        DateTimeOffset.FromFileTime(r.GetInt64(11)).ToUniversalTime());

    private static SavedFilter ReadFilter(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        JsonSerializer.Deserialize<List<QueryNode>>(r.GetString(2), Json) ?? [],
        DateTimeOffset.FromFileTime(r.GetInt64(3)).ToUniversalTime(),
        DateTimeOffset.FromFileTime(r.GetInt64(4)).ToUniversalTime());

    private static ColumnLayout ReadLayout(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        JsonSerializer.Deserialize<List<ColumnSetting>>(r.GetString(2), Json) ?? []);

    private async Task<List<T>> QueryAsync<T>(string sql, Action<SqliteCommand>? bind, Func<SqliteDataReader, T> map, CancellationToken ct)
    {
        var connection = await _appDb.OpenAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            bind?.Invoke(cmd);
            var list = new List<T>();
            var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    list.Add(map(reader));
                }
            }

            return list;
        }
    }

    private Task<int> ExecuteAsync(string sql, Action<SqliteCommand> bind, CancellationToken ct) =>
        _writer.ExecuteAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            bind(cmd);
            return cmd.ExecuteNonQuery();
        }, ct);
}
