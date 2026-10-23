using System.Collections;
using System.Text.Json.Serialization;

namespace NexPane.Core.Search;

/// <summary>Query fields of the grammar (§8.1). <see cref="Default"/> is a bare term without a field.</summary>
public enum QueryField
{
    Default,
    Name,
    Ext,
    Size,
    Modified,
    Created,
    Accessed,
    Content,
    Path,
    Parent,
    Type,
    Regex,
    Tag,
    Attr,
    Depth,
    Len,
    Category,
    Semantic,
    Dupe,
}

public enum ComparisonOperator
{
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    Equal,
    NotEqual,
}

/// <summary>Case mode of a regex literal: no suffix (default, case-insensitive), <c>i</c>, or <c>c</c> (case-sensitive).</summary>
public enum RegexCaseMode
{
    Default,
    IgnoreCase,
    CaseSensitive,
}

/// <summary>Root of the canonical query AST (§8.3). Records compare structurally, so <c>Parse(Print(ast)) == ast</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$k")]
[JsonDerivedType(typeof(AndNode), "and")]
[JsonDerivedType(typeof(OrNode), "or")]
[JsonDerivedType(typeof(NotNode), "not")]
[JsonDerivedType(typeof(TermNode), "term")]
public abstract record QueryNode;

/// <summary>Conjunction of two or more operands (juxtaposition, <c>AND</c>, <c>&amp;</c>).</summary>
public sealed record AndNode(NodeList Children) : QueryNode
{
    public static AndNode Of(params QueryNode[] children) => new(new NodeList(children));
}

/// <summary>Disjunction of two or more operands (<c>OR</c>, <c>|</c>).</summary>
public sealed record OrNode(NodeList Children) : QueryNode
{
    public static OrNode Of(params QueryNode[] children) => new(new NodeList(children));
}

public sealed record NotNode(QueryNode Operand) : QueryNode;

/// <summary><c>field:value</c> or a bare value (<see cref="QueryField.Default"/>).</summary>
public sealed record TermNode(QueryField Field, QueryValue Value) : QueryNode;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$v")]
[JsonDerivedType(typeof(TextValue), "text")]
[JsonDerivedType(typeof(RegexValue), "regex")]
[JsonDerivedType(typeof(ComparisonValue), "cmp")]
[JsonDerivedType(typeof(RangeValue), "range")]
[JsonDerivedType(typeof(ListValue), "list")]
public abstract record QueryValue;

/// <summary>A scalar: bare or quoted text.</summary>
public sealed record TextValue(string Text, bool Quoted = false) : QueryValue;

/// <summary><c>/pattern/flags</c>; <c>\/</c> inside the literal is stored as <c>/</c>.</summary>
public sealed record RegexValue(string Pattern, RegexCaseMode CaseMode = RegexCaseMode.Default) : QueryValue;

public sealed record ComparisonValue(ComparisonOperator Operator, TextValue Operand) : QueryValue;

/// <summary><c>low..high</c> (inclusive bounds; dates cover the whole high period).</summary>
public sealed record RangeValue(TextValue Low, TextValue High) : QueryValue;

/// <summary><c>a;b;c</c> (any of).</summary>
public sealed record ListValue(TextList Items) : QueryValue
{
    public static ListValue Of(params TextValue[] items) => new(new TextList(items));
}

/// <summary>Immutable list with structural equality (records compare their members with it).</summary>
public class EquatableList<T> : IReadOnlyList<T>, IEquatable<EquatableList<T>>
{
    private readonly T[] _items;

    public EquatableList(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = items.ToArray();
    }

    public int Count => _items.Length;

    public T this[int index] => _items[index];

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public bool Equals(EquatableList<T>? other) =>
        other is not null && _items.AsSpan().SequenceEqual(other._items, EqualityComparer<T>.Default);

    public override bool Equals(object? obj) => obj is EquatableList<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}

[JsonConverter(typeof(NodeListJsonConverter))]
public sealed class NodeList(IEnumerable<QueryNode> items) : EquatableList<QueryNode>(items);

[JsonConverter(typeof(TextListJsonConverter))]
public sealed class TextList(IEnumerable<TextValue> items) : EquatableList<TextValue>(items);

internal sealed class NodeListJsonConverter : JsonConverter<NodeList>
{
    public override NodeList Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) =>
        new(System.Text.Json.JsonSerializer.Deserialize<List<QueryNode>>(ref reader, options) ?? []);

    public override void Write(System.Text.Json.Utf8JsonWriter writer, NodeList value, System.Text.Json.JsonSerializerOptions options) =>
        System.Text.Json.JsonSerializer.Serialize(writer, value.ToList(), options);
}

internal sealed class TextListJsonConverter : JsonConverter<TextList>
{
    public override TextList Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) =>
        new(System.Text.Json.JsonSerializer.Deserialize<List<TextValue>>(ref reader, options) ?? []);

    public override void Write(System.Text.Json.Utf8JsonWriter writer, TextList value, System.Text.Json.JsonSerializerOptions options) =>
        System.Text.Json.JsonSerializer.Serialize(writer, value.ToList(), options);
}

/// <summary>Half-open character range in the query text.</summary>
public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;
}

public enum DiagnosticSeverity
{
    Warning,
    Error,
}

public sealed record QueryDiagnostic(DiagnosticSeverity Severity, string Message, TextSpan Span);

/// <summary>Parser output: best-effort AST (null for an empty query), diagnostics with spans, and node spans.</summary>
public sealed record ParseResult(QueryNode? Ast, IReadOnlyList<QueryDiagnostic> Diagnostics, IReadOnlyDictionary<QueryNode, TextSpan> Spans)
{
    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}

public interface IQueryParser
{
    ParseResult Parse(string text);
}

public interface IQueryPrinter
{
    string Print(QueryNode ast);
}
