using NexPane.Core.Search;
using NexPane.Search.Execution;
using NexPane.Search.Language;

namespace NexPane.Search.Tests;

public sealed class PlannerTests
{
    private static readonly QueryParser Parser = new();
    private static readonly QueryPlanner Planner = new(new SearchEngineOptions(), TimeProvider.System, new MapCategories());

    [Theory]
    [InlineData("report", "memory=name~REPORT")]
    [InlineData("report ext:pdf", "memory=AND[ext:PDF, name~REPORT]")]
    [InlineData("/x/ size:>1MB report", "memory=AND[size, name~REPORT, regex:x]")]
    [InlineData("path:src *.cs attr:hidden", "memory=AND[attr:hidden, glob:*.CS, path~SRC]")]
    [InlineData("a | ext:pdf", "memory=OR[ext:PDF, name~A]")]
    [InlineData("-a", "memory=NOT name~A")]
    [InlineData("ext:pdf content:\"invoice total\"", "memory=AND[ext:PDF, content:invoice total] fts=[\"invoice total\"]")]
    [InlineData("content:invoice total2", "memory=AND[name~TOTAL2, content:invoice] fts=[\"invoice\"]")]
    [InlineData("ext:txt content:/total:\\s*\\d+/", "memory=ext:TXT post=[content-regex]")]
    [InlineData("type:image dupe:sha256", "memory=type:image post=[dupe]")]
    [InlineData("dupe:name content:/x/ accessed:today", "memory=true post=[accessed, content-regex, dupe]")]
    [InlineData("semantic:\"tax papers\" ext:pdf", "memory=AND[ext:PDF, semantic:tax papers] semantic=[tax papers]")]
    [InlineData("tag:urgent;later", "memory=tag:urgent;later tags=[urgent, later]")]
    [InlineData("category:Docs report", "memory=AND[OR[ext:PDF, ext:DOCX], name~REPORT]")]
    public void Plan_shape(string query, string expected)
    {
        var plan = Planner.Plan(Parser.Parse(query).Ast!);

        plan.HasErrors.Should().BeFalse(string.Join("; ", plan.Diagnostics.Select(d => d.Message)));
        Normalize(plan.Describe()).Should().Be(Normalize(expected));
    }

    [Theory]
    [InlineData("report", false)]
    [InlineData("content:x", true)]
    [InlineData("semantic:x", true)]
    [InlineData("ext:txt content:/x/", true)]
    [InlineData("ext:pdf size:>1MB", false)]
    public void Content_and_semantic_terms_require_debounce(string query, bool debounce)
    {
        Planner.Plan(Parser.Parse(query).Ast!).RequiresDebounce.Should().Be(debounce);
    }

    [Theory]
    [InlineData("rep", "repo", true)]
    [InlineData("rep", "report", true)]
    [InlineData("rep", "prep", true)]
    [InlineData("rep", "re", false)]
    [InlineData("rep", "rep ext:pdf", true)]
    [InlineData("rep ext:pdf", "repo ext:pdf", true)]
    [InlineData("rep ext:pdf", "repo", false)]
    [InlineData("rep", "rep*", false)]
    [InlineData("rep*", "rep*x", false)]
    [InlineData("a | b", "a", true)]
    [InlineData("a", "a | b", false)]
    [InlineData("ext:jpg;png", "ext:jpg", true)]
    [InlineData("ext:jpg", "ext:jpg;png", false)]
    [InlineData("path:src", "path:src\\app", true)]
    [InlineData("size:>1MB", "size:>1MB", true)]
    [InlineData("size:>1MB", "size:>2MB", false)]
    [InlineData("-x", "-xy", false)]
    [InlineData("REP", "report", true)]
    public void Refinement_detection(string previous, string next, bool refinement)
    {
        QueryPlanner.IsRefinementOf(Parser.Parse(previous).Ast!, Parser.Parse(next).Ast!).Should().Be(refinement);
        Planner.Plan(Parser.Parse(next).Ast!, Parser.Parse(previous).Ast!).IsRefinement.Should().Be(refinement);
    }

    [Fact]
    public void Recursive_categories_are_rejected()
    {
        var plan = Planner.Plan(Parser.Parse("category:Loop1").Ast!);

        plan.Diagnostics.Should().ContainSingle(d => d.Message.Contains("refers to itself", StringComparison.Ordinal) && d.Message.Contains("Loop1 → Loop2 → Loop1", StringComparison.Ordinal));
    }

    [Fact]
    public void Self_referencing_category_is_rejected()
    {
        Planner.Plan(Parser.Parse("category:Self").Ast!).Diagnostics.Should().ContainSingle(d => d.Message.Contains("refers to itself", StringComparison.Ordinal));
    }

    [Fact]
    public void Nested_categories_expand()
    {
        var plan = Planner.Plan(Parser.Parse("category:BigDocs").Ast!);

        plan.HasErrors.Should().BeFalse();
        Normalize(plan.Describe()).Should().Be(Normalize("memory=AND[size, OR[ext:PDF, ext:DOCX]]"));
    }

    [Theory]
    [InlineData("invoice", "\"invoice\"")]
    [InlineData("invoice total", "\"invoice\" \"total\"")]
    [InlineData("\"invoice total\"", "\"invoice total\"")]
    [InlineData("inv*", "\"inv\"*")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("NEAR(a b)", "\"NEAR(a\" \"b)\"")]
    public void Fts_queries_quote_user_tokens(string text, string fts)
    {
        var quoted = text.StartsWith('"');
        QueryPlanner.ToFtsQuery(new TextValue(quoted ? text.Trim('"') : text, quoted)).Should().Be(fts);
    }

    private static string Normalize(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private sealed class MapCategories : ICategoryResolver
    {
        private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Docs"] = "ext:pdf | ext:docx",
            ["BigDocs"] = "size:>1MB category:Docs",
            ["Loop1"] = "a category:Loop2",
            ["Loop2"] = "b category:Loop1",
            ["Self"] = "x | category:Self",
        };

        public bool TryGetCategoryAst(string name, out QueryNode ast)
        {
            if (_map.TryGetValue(name, out var text))
            {
                ast = Parser.Parse(text).Ast!;
                return true;
            }

            ast = null!;
            return false;
        }
    }
}
