using NexPane.Core.Search;

namespace NexPane.Search.Language;

/// <summary>Field names and the value forms each field accepts (§8.1, §8.2).</summary>
public static class QueryFields
{
    private static readonly Dictionary<string, QueryField> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = QueryField.Name,
        ["ext"] = QueryField.Ext,
        ["size"] = QueryField.Size,
        ["modified"] = QueryField.Modified,
        ["created"] = QueryField.Created,
        ["accessed"] = QueryField.Accessed,
        ["content"] = QueryField.Content,
        ["path"] = QueryField.Path,
        ["parent"] = QueryField.Parent,
        ["type"] = QueryField.Type,
        ["regex"] = QueryField.Regex,
        ["tag"] = QueryField.Tag,
        ["attr"] = QueryField.Attr,
        ["depth"] = QueryField.Depth,
        ["len"] = QueryField.Len,
        ["category"] = QueryField.Category,
        ["semantic"] = QueryField.Semantic,
        ["dupe"] = QueryField.Dupe,
    };

    public static IReadOnlyCollection<string> Names => ByName.Keys;

    public static bool TryGet(ReadOnlySpan<char> name, out QueryField field) =>
        ByName.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out field);

    public static string NameOf(QueryField field) => field switch
    {
        QueryField.Default => string.Empty,
        _ => field.ToString().ToLowerInvariant(),
    };

    /// <summary><c>a;b</c> lists.</summary>
    public static bool AllowsList(QueryField field) => field is QueryField.Ext or QueryField.Type or QueryField.Tag or QueryField.Attr;

    /// <summary><c>&lt;x</c> comparisons and <c>a..b</c> ranges.</summary>
    public static bool AllowsComparison(QueryField field) =>
        field is QueryField.Size or QueryField.Modified or QueryField.Created or QueryField.Accessed or QueryField.Depth or QueryField.Len;

    /// <summary><c>/regex/</c> literals.</summary>
    public static bool AllowsRegex(QueryField field) =>
        field is QueryField.Default or QueryField.Name or QueryField.Path or QueryField.Regex or QueryField.Content;
}
