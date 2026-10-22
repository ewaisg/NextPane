using System.Globalization;
using NexPane.Core.Indexing;
using NexPane.Core.Search;

namespace NexPane.Search.Execution;

/// <summary>A <c>content:</c> full-text term resolved through FTS5 before the scan.</summary>
public sealed record FullTextLookup(string FtsQuery, SetPredicate Target);

/// <summary>A <c>semantic:</c> term resolved through the vector index before the scan.</summary>
public sealed record SemanticLookup(string Text, SetPredicate Target);

/// <summary>A <c>tag:</c> term resolved from app.db before the scan.</summary>
public sealed record TagLookupRequest(IReadOnlyList<string> Tags, SetPredicate Target);

/// <summary>Work done after the in-memory scan, on the candidate set (only valid in conjunctive position).</summary>
public abstract record PostFilter(string Description);

public sealed record AccessedFilter(IntervalSet Set) : PostFilter("accessed");

public sealed record ContentRegexFilter(UserRegex Regex) : PostFilter("content-regex");

[Flags]
public enum DupeKeys
{
    None = 0,
    Name = 1,
    Size = 2,
    Sha256 = 4,
}

public sealed record DupeFilter(DupeKeys Keys) : PostFilter("dupe");

/// <summary>Execution plan of one query (§6.2): memory predicate tree (cost-ordered), pre-scan lookups (FTS, semantic,
/// tags) bound into set predicates, and post-filters (accessed stat, content regex streaming scan, dupe).</summary>
public sealed class QueryPlan
{
    public required QueryNode Ast { get; init; }

    public required Predicate Memory { get; init; }

    public IReadOnlyList<FullTextLookup> FullText { get; init; } = [];

    public IReadOnlyList<SemanticLookup> Semantic { get; init; } = [];

    public IReadOnlyList<TagLookupRequest> Tags { get; init; } = [];

    public IReadOnlyList<PostFilter> PostFilters { get; init; } = [];

    public IReadOnlyList<QueryDiagnostic> Diagnostics { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>True when the query is a strict refinement of the previous one (evaluate over its result ids only).</summary>
    public bool IsRefinement { get; init; }

    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    /// <summary>Content and semantic terms debounce 150 ms in the UI; filename-only queries run on every keystroke (§6.2).</summary>
    public bool RequiresDebounce => FullText.Count > 0 || Semantic.Count > 0 || PostFilters.Any(p => p is ContentRegexFilter);

    public string Describe()
    {
        var parts = new List<string> { "memory=" + Memory.Describe() };
        if (FullText.Count > 0)
        {
            parts.Add("fts=[" + string.Join(", ", FullText.Select(f => f.FtsQuery)) + "]");
        }

        if (Semantic.Count > 0)
        {
            parts.Add("semantic=[" + string.Join(", ", Semantic.Select(s => s.Text)) + "]");
        }

        if (Tags.Count > 0)
        {
            parts.Add("tags=[" + string.Join(", ", Tags.SelectMany(t => t.Tags)) + "]");
        }

        if (PostFilters.Count > 0)
        {
            parts.Add("post=[" + string.Join(", ", PostFilters.Select(p => p.Description)) + "]");
        }

        if (IsRefinement)
        {
            parts.Add("refine");
        }

        return string.Join(" ", parts);
    }
}

/// <summary>
/// Builds <see cref="QueryPlan"/>s (P3.24): expands <c>category:</c> macros (rejecting recursion), compiles field
/// evaluators (P3.21–P3.23), splits memory / FTS / semantic / post-filter work, orders AND/OR operands by cost and
/// detects incremental refinement of the previous query.
/// </summary>
public sealed class QueryPlanner(SearchEngineOptions options, TimeProvider clock, ICategoryResolver? categories = null)
{
    private const int MaxCategoryDepth = 16;

    public QueryPlan Plan(QueryNode ast, QueryNode? previous = null, IReadOnlyDictionary<QueryNode, TextSpan>? spans = null)
    {
        ArgumentNullException.ThrowIfNull(ast);
        var context = new CompileContext(spans);
        var expanded = Expand(ast, context, []);
        var memory = Compile(expanded, conjunctive: true, context);
        return new QueryPlan
        {
            Ast = expanded,
            Memory = memory,
            FullText = context.FullText,
            Semantic = context.Semantic,
            Tags = context.Tags,
            PostFilters = context.PostFilters.OrderBy(PostFilterOrder).ToList(),
            Diagnostics = context.Diagnostics,
            Warnings = context.Warnings.Distinct().ToList(),
            IsRefinement = previous is not null && context.PostFilters.Count == 0 && context.FullText.Count == 0 && context.Semantic.Count == 0 &&
                           IsRefinementOf(previous, expanded),
        };
    }

    // ===================================================================== refinement

    /// <summary>True when every entry matching <paramref name="next"/> also matches <paramref name="previous"/>.</summary>
    public static bool IsRefinementOf(QueryNode previous, QueryNode next)
    {
        if (previous.Equals(next))
        {
            return true;
        }

        switch (next)
        {
            case AndNode nextAnd when previous is AndNode prevAnd:
                return prevAnd.Children.All(p => nextAnd.Children.Any(n => IsRefinementOf(p, n)));
            case AndNode nextAnd:
                return nextAnd.Children.Any(n => IsRefinementOf(previous, n));
        }

        if (previous is OrNode prevOr)
        {
            return prevOr.Children.Any(p => IsRefinementOf(p, next));
        }

        if (previous is TermNode p1 && next is TermNode n1)
        {
            return TermImplies(n1, p1);
        }

        return false;
    }

    private static bool TermImplies(TermNode next, TermNode previous)
    {
        var nameLike = (QueryField f) => f is QueryField.Default or QueryField.Name;
        if (nameLike(next.Field) && nameLike(previous.Field) &&
            next.Value is TextValue nt && previous.Value is TextValue pt &&
            !GlobMatcher.HasWildcards(nt.Text) && !GlobMatcher.HasWildcards(pt.Text))
        {
            // Substring semantics: a name containing "repo" also contains "rep".
            return nt.Text.Contains(pt.Text, StringComparison.OrdinalIgnoreCase);
        }

        if (next.Field == QueryField.Path && previous.Field == QueryField.Path && next.Value is TextValue np && previous.Value is TextValue pp)
        {
            return np.Text.Contains(pp.Text, StringComparison.OrdinalIgnoreCase);
        }

        if (next.Field == QueryField.Ext && previous.Field == QueryField.Ext)
        {
            var prevSet = Items(previous.Value).Select(s => s.TrimStart('.')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Items(next.Value).All(e => prevSet.Contains(e.TrimStart('.')));
        }

        return false;
    }

    private static IEnumerable<string> Items(QueryValue value) => value switch
    {
        TextValue t => [t.Text],
        ListValue l => l.Items.Select(i => i.Text),
        _ => [],
    };

    // ===================================================================== category expansion

    private QueryNode Expand(QueryNode node, CompileContext context, List<string> stack)
    {
        switch (node)
        {
            case AndNode and:
                return new AndNode(new NodeList(and.Children.Select(c => Expand(c, context, stack))));
            case OrNode or:
                return new OrNode(new NodeList(or.Children.Select(c => Expand(c, context, stack))));
            case NotNode not:
                return new NotNode(Expand(not.Operand, context, stack));
            case TermNode { Field: QueryField.Category } term:
            {
                var name = (term.Value as TextValue)?.Text;
                if (name is null)
                {
                    context.Error(term, "category: expects a category name.");
                    return term;
                }

                if (stack.Contains(name, StringComparer.OrdinalIgnoreCase) || stack.Count >= MaxCategoryDepth)
                {
                    context.Error(term, $"Category '{name}' refers to itself ({string.Join(" → ", stack.Append(name))}).");
                    return term;
                }

                if (categories is null || !categories.TryGetCategoryAst(name, out var ast))
                {
                    context.Error(term, $"Unknown category '{name}'.");
                    return term;
                }

                stack.Add(name);
                var expanded = Expand(ast, context, stack);
                stack.RemoveAt(stack.Count - 1);
                return expanded;
            }

            default:
                return node;
        }
    }

    // ===================================================================== compilation

    private Predicate Compile(QueryNode node, bool conjunctive, CompileContext context)
    {
        switch (node)
        {
            case AndNode and:
                return Simplify(new AndPredicate(and.Children.Select(c => Compile(c, conjunctive, context)).ToList()));
            case OrNode or:
                return Simplify(new OrPredicate(or.Children.Select(c => Compile(c, conjunctive: false, context)).ToList()));
            case NotNode not:
                return new NotPredicate(Compile(not.Operand, conjunctive: false, context));
            case TermNode term:
                return CompileTerm(term, conjunctive, context);
            default:
                throw new ArgumentException($"Unknown node type {node.GetType().Name}.", nameof(node));
        }
    }

    private static Predicate Simplify(AndPredicate and)
    {
        var operands = and.Operands.Where(o => o is not ConstantPredicate { Value: true }).ToList();
        if (operands.Any(o => o is ConstantPredicate { Value: false }))
        {
            return ConstantPredicate.False;
        }

        return operands.Count switch
        {
            0 => ConstantPredicate.True,
            1 => operands[0],
            _ => new AndPredicate(operands),
        };
    }

    private static Predicate Simplify(OrPredicate or)
    {
        if (or.Operands.Any(o => o is ConstantPredicate { Value: true }))
        {
            return ConstantPredicate.True;
        }

        var operands = or.Operands.Where(o => o is not ConstantPredicate { Value: false }).ToList();
        return operands.Count switch
        {
            0 => ConstantPredicate.False,
            1 => operands[0],
            _ => new OrPredicate(operands),
        };
    }

    private Predicate CompileTerm(TermNode term, bool conjunctive, CompileContext context)
    {
        var value = term.Value;
        switch (term.Field)
        {
            case QueryField.Default:
            case QueryField.Name:
                return value switch
                {
                    RegexValue r => CompileNameRegex(term, r.Pattern, r.CaseMode, context),
                    TextValue t when !t.Quoted && GlobMatcher.HasWildcards(t.Text) => new NameGlobPredicate(NameArena.Fold(t.Text)),
                    TextValue t => new NameSubstringPredicate(NameArena.Fold(t.Text)),
                    _ => Invalid(term, context, "Name terms take text or a /regex/."),
                };

            case QueryField.Regex:
                return value switch
                {
                    RegexValue r => CompileNameRegex(term, r.Pattern, r.CaseMode, context),
                    TextValue t => CompileNameRegex(term, t.Text, RegexCaseMode.Default, context),
                    _ => Invalid(term, context, "regex: takes a pattern."),
                };

            case QueryField.Ext:
            {
                var items = TextItems(value);
                if (items is null)
                {
                    return Invalid(term, context, "ext: takes extensions such as ext:pdf or ext:jpg;png.");
                }

                var exts = items.Select(e => NameArena.Fold(e.TrimStart('.'))).Where(e => e.Length > 0).ToList();
                return new ExtensionPredicate(exts, "ext:" + string.Join(';', exts));
            }

            case QueryField.Size:
                return SizeParser.TryParse(value, out var sizes, out var sizeError)
                    ? new NumericPredicate(NumericColumn.Size, sizes, "size")
                    : Invalid(term, context, sizeError!);

            case QueryField.Modified:
            case QueryField.Created:
                return DateParser.TryParse(value, clock, out var dates, out var dateError)
                    ? new NumericPredicate(term.Field == QueryField.Modified ? NumericColumn.Modified : NumericColumn.Created, dates, QueryFieldName(term.Field))
                    : Invalid(term, context, dateError!);

            case QueryField.Accessed:
                if (!DateParser.TryParse(value, clock, out var accessed, out var accessedError))
                {
                    return Invalid(term, context, accessedError!);
                }

                return PostFilterOrError(term, conjunctive, context, new AccessedFilter(accessed));

            case QueryField.Len:
            case QueryField.Depth:
                return TryIntegers(value, out var ints)
                    ? new NumericPredicate(term.Field == QueryField.Len ? NumericColumn.Length : NumericColumn.Depth, ints, QueryFieldName(term.Field))
                    : Invalid(term, context, $"{QueryFieldName(term.Field)}: takes a whole number, comparison or range (e.g. <3, 2..4).");

            case QueryField.Attr:
                return CompileAttributes(term, context);

            case QueryField.Type:
                return CompileType(term, context);

            case QueryField.Path:
                return value switch
                {
                    RegexValue r => RegexFactory.TryCreate(r.Pattern, r.CaseMode, out var regex, out var error)
                        ? WithSlowWarning(new PathRegexPredicate(regex!), regex!, context)
                        : Invalid(term, context, error!),
                    TextValue t => new PathSubstringPredicate(NameArena.Fold(t.Text)),
                    _ => Invalid(term, context, "path: takes text or a /regex/."),
                };

            case QueryField.Parent:
                return value is TextValue parent
                    ? new UnderDirectoryPredicate([parent.Text], directChildrenOnly: true, "parent:" + parent.Text)
                    : Invalid(term, context, "parent: takes a folder path.");

            case QueryField.Tag:
            {
                var tags = TextItems(value);
                if (tags is null)
                {
                    return Invalid(term, context, "tag: takes tag names (tag:a;b).");
                }

                var target = new SetPredicate("tag:" + string.Join(';', tags));
                context.Tags.Add(new TagLookupRequest(tags, target));
                return target;
            }

            case QueryField.Content:
                switch (value)
                {
                    case TextValue t when t.Text.Trim().Length > 0:
                    {
                        var target = new SetPredicate("content:" + t.Text);
                        context.FullText.Add(new FullTextLookup(ToFtsQuery(t), target));
                        return target;
                    }

                    case RegexValue r:
                        return RegexFactory.TryCreate(r.Pattern, r.CaseMode, out var contentRegex, out var contentError)
                            ? PostFilterOrError(term, conjunctive, context, new ContentRegexFilter(contentRegex!), contentRegex!.SlowMode)
                            : Invalid(term, context, contentError!);
                    default:
                        return Invalid(term, context, "content: takes words, a \"phrase\" or a /regex/.");
                }

            case QueryField.Semantic:
                if (value is TextValue semantic && semantic.Text.Trim().Length > 0)
                {
                    var target = new SetPredicate("semantic:" + semantic.Text);
                    context.Semantic.Add(new SemanticLookup(semantic.Text, target));
                    return target;
                }

                return Invalid(term, context, "semantic: takes a description.");

            case QueryField.Dupe:
            {
                var keys = DupeKeys.None;
                foreach (var item in TextItems(value) ?? [])
                {
                    keys |= item.ToLowerInvariant() switch
                    {
                        "name" => DupeKeys.Name,
                        "size" => DupeKeys.Size,
                        "sha256" or "hash" or "content" => DupeKeys.Sha256 | DupeKeys.Size,
                        _ => DupeKeys.None,
                    };
                }

                return keys == DupeKeys.None
                    ? Invalid(term, context, "dupe: takes name, size or sha256.")
                    : PostFilterOrError(term, conjunctive, context, new DupeFilter(keys));
            }

            case QueryField.Category:
                // Unresolved after expansion: already reported.
                return ConstantPredicate.False;

            default:
                return Invalid(term, context, "Unsupported field.");
        }
    }

    private static ConstantPredicate PostFilterOrError(TermNode term, bool conjunctive, CompileContext context, PostFilter filter, bool slow = false)
    {
        if (!conjunctive)
        {
            return Invalid(term, context, $"{QueryFieldName(term.Field)}: can only be combined with AND (not inside OR or NOT).");
        }

        if (slow)
        {
            context.Warnings.Add(RegexFactory.SlowModeWarning);
        }

        context.PostFilters.Add(filter);
        return ConstantPredicate.True;
    }

    private static int PostFilterOrder(PostFilter filter) => filter switch
    {
        AccessedFilter => 0,
        ContentRegexFilter => 1,
        _ => 2,
    };

    private static Predicate CompileNameRegex(TermNode term, string pattern, RegexCaseMode mode, CompileContext context) =>
        RegexFactory.TryCreate(pattern, mode, out var regex, out var error)
            ? WithSlowWarning(new NameRegexPredicate(regex!), regex!, context)
            : Invalid(term, context, error!);

    private static Predicate WithSlowWarning(Predicate predicate, UserRegex regex, CompileContext context)
    {
        if (regex.SlowMode)
        {
            context.Warnings.Add(RegexFactory.SlowModeWarning);
        }

        return predicate;
    }

    private static readonly Dictionary<string, uint> AttributeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["readonly"] = 0x1, ["r"] = 0x1,
        ["hidden"] = 0x2, ["h"] = 0x2,
        ["system"] = 0x4, ["s"] = 0x4,
        ["directory"] = 0x10, ["d"] = 0x10,
        ["archive"] = 0x20, ["a"] = 0x20,
        ["temporary"] = 0x100, ["t"] = 0x100,
        ["sparse"] = 0x200,
        ["reparse"] = 0x400, ["reparsepoint"] = 0x400, ["link"] = 0x400, ["l"] = 0x400,
        ["compressed"] = 0x800, ["c"] = 0x800,
        ["offline"] = 0x1000, ["o"] = 0x1000,
        ["notindexed"] = 0x2000, ["i"] = 0x2000,
        ["encrypted"] = 0x4000, ["e"] = 0x4000,
        ["pinned"] = 0x80000, ["p"] = 0x80000,
        ["unpinned"] = 0x100000, ["u"] = 0x100000,
        ["recall"] = 0x400000,
    };

    private static Predicate CompileAttributes(TermNode term, CompileContext context)
    {
        var items = TextItems(term.Value);
        if (items is null)
        {
            return Invalid(term, context, "attr: takes attribute names such as hidden, readonly or !system.");
        }

        var predicates = new List<Predicate>();
        foreach (var raw in items)
        {
            var negate = raw.StartsWith('!');
            var name = negate ? raw[1..] : raw;
            if (!AttributeNames.TryGetValue(name, out var mask))
            {
                return Invalid(term, context, $"Unknown attribute '{name}'. Known: {string.Join(", ", AttributeNames.Keys.Where(k => k.Length > 1))}.");
            }

            predicates.Add(new AttributePredicate(mask, negate, "attr:" + raw));
        }

        return predicates.Count == 1 ? predicates[0] : new OrPredicate(predicates);
    }

    private Predicate CompileType(TermNode term, CompileContext context)
    {
        var items = TextItems(term.Value);
        if (items is null)
        {
            return Invalid(term, context, "type: takes a category such as folder, file, image or document.");
        }

        var predicates = new List<Predicate>();
        foreach (var item in items)
        {
            if (item.Equals("folder", StringComparison.OrdinalIgnoreCase) || item.Equals("dir", StringComparison.OrdinalIgnoreCase))
            {
                predicates.Add(new KindPredicate(directories: true));
            }
            else if (item.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                predicates.Add(new KindPredicate(directories: false));
            }
            else if (options.TypeMap.TryGetValue(item, out var exts))
            {
                predicates.Add(new ExtensionPredicate(exts, "type:" + item.ToLowerInvariant()));
            }
            else
            {
                return Invalid(term, context, $"Unknown type '{item}'. Known: folder, file, {string.Join(", ", options.TypeMap.Keys.Order(StringComparer.Ordinal))}.");
            }
        }

        return predicates.Count == 1 ? predicates[0] : new OrPredicate(predicates);
    }

    private static List<string>? TextItems(QueryValue value) => value switch
    {
        TextValue t => [t.Text],
        ListValue l => l.Items.Select(i => i.Text).ToList(),
        _ => null,
    };

    private static bool TryIntegers(QueryValue value, out IntervalSet set)
    {
        set = new IntervalSet([]);
        switch (value)
        {
            case TextValue t when long.TryParse(t.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var n):
                set = new IntervalSet([new Interval(n, n + 1)]);
                return true;
            case ComparisonValue c when long.TryParse(c.Operand.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var n):
                set = SizeParser.Compare(c.Operator, new Interval(n, n + 1));
                return true;
            case RangeValue r when long.TryParse(r.Low.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var lo) &&
                                   long.TryParse(r.High.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var hi):
                set = new IntervalSet([new Interval(lo, hi + 1)]);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Quoted → FTS5 phrase; bare → every token must occur (implicit AND), each token quoted so FTS5
    /// operators in user text are literal.</summary>
    public static string ToFtsQuery(TextValue value)
    {
        static string Quote(string token) => "\"" + token.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        if (value.Quoted)
        {
            return Quote(value.Text);
        }

        var tokens = value.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', tokens.Select(t => t.EndsWith('*') && t.Length > 1 ? Quote(t[..^1]) + "*" : Quote(t)));
    }

    private static string QueryFieldName(QueryField field) => field.ToString().ToLowerInvariant();

    private static ConstantPredicate Invalid(TermNode term, CompileContext context, string message)
    {
        context.Error(term, message);
        return ConstantPredicate.False;
    }

    private sealed class CompileContext(IReadOnlyDictionary<QueryNode, TextSpan>? spans)
    {
        public List<FullTextLookup> FullText { get; } = [];

        public List<SemanticLookup> Semantic { get; } = [];

        public List<TagLookupRequest> Tags { get; } = [];

        public List<PostFilter> PostFilters { get; } = [];

        public List<QueryDiagnostic> Diagnostics { get; } = [];

        public List<string> Warnings { get; } = [];

        public void Error(QueryNode node, string message)
        {
            var span = spans is not null && spans.TryGetValue(node, out var s) ? s : new TextSpan(0, 0);
            Diagnostics.Add(new QueryDiagnostic(DiagnosticSeverity.Error, message, span));
        }
    }
}
