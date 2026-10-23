using NexPane.Core.Search;
using NexPane.Search.Language;

namespace NexPane.Search.Tests;

public sealed class QueryParserTests
{
    private static readonly QueryParser Parser = new();

    [Theory]
    // --- bare terms and juxtaposition
    [InlineData("report", "report")]
    [InlineData("  report  ", "report")]
    [InlineData("a b", "(and a b)")]
    [InlineData("a b c", "(and a b c)")]
    [InlineData("*.log", "*.log")]
    [InlineData("IMG_????.jpg", "IMG_????.jpg")]
    [InlineData("R&D.docx", "R&D.docx")]
    [InlineData("a|b", "a|b")]
    [InlineData("a&b", "a&b")]
    [InlineData("C:\\Work\\x", "C:\\Work\\x")]
    [InlineData("foo:bar", "foo:bar")]
    [InlineData("ANDROID", "ANDROID")]
    [InlineData("ORACLE notes", "(and ORACLE notes)")]
    [InlineData("NOTICE", "NOTICE")]
    [InlineData("and or not", "(and and or not)")]
    [InlineData("-", "-")]
    [InlineData("a - b", "(and a - b)")]
    [InlineData("été café", "(and été café)")]
    [InlineData("日本語", "日本語")]
    [InlineData("x.tar.gz", "x.tar.gz")]
    [InlineData("a..b", "a..b")]
    [InlineData("a;b", "a;b")]
    // --- precedence
    [InlineData("a b | c", "(or (and a b) c)")]
    [InlineData("a b OR c", "(or (and a b) c)")]
    [InlineData("a | b c", "(or a (and b c))")]
    [InlineData("a OR b OR c", "(or a b c)")]
    [InlineData("a AND b OR c AND d", "(or (and a b) (and c d))")]
    [InlineData("a & b | c & d", "(or (and a b) (and c d))")]
    [InlineData("a AND b c", "(and a b c)")]
    [InlineData("a (b | c)", "(and a (or b c))")]
    [InlineData("(a b) c", "(and (and a b) c)")]
    [InlineData("(a | b) (c | d)", "(and (or a b) (or c d))")]
    [InlineData("((a))", "a")]
    [InlineData("(a)", "a")]
    [InlineData("(a b)", "(and a b)")]
    [InlineData("a | (b | c)", "(or a (or b c))")]
    [InlineData("((a | b) c) | d", "(or (and (or a b) c) d)")]
    // --- NOT binding
    [InlineData("NOT a", "(not a)")]
    [InlineData("-a", "(not a)")]
    [InlineData("!a", "(not a)")]
    [InlineData("NOT a b", "(and (not a) b)")]
    [InlineData("a -b", "(and a (not b))")]
    [InlineData("NOT a | b", "(or (not a) b)")]
    [InlineData("NOT (a | b)", "(not (or a b))")]
    [InlineData("-(a b)", "(not (and a b))")]
    [InlineData("NOT NOT a", "(not (not a))")]
    [InlineData("--a", "(not (not a))")]
    [InlineData("-!a", "(not (not a))")]
    [InlineData("-ext:pdf", "(not ext:pdf)")]
    [InlineData("a AND NOT b", "(and a (not b))")]
    [InlineData("NOT\ta", "(not a)")]
    [InlineData("a NOT b", "(and a (not b))")]
    // --- quoted strings and escapes
    [InlineData("\"q3 report\"", "\"q3 report\"")]
    [InlineData("\"a\\\"b\"", "\"a\"b\"")]
    [InlineData("\"C:\\Work\"", "\"C:\\Work\"")]
    [InlineData("\"C:\\Work\\\\\"", "\"C:\\Work\\\"")]
    [InlineData("\"back\\\\\\\"slash\"", "\"back\\\"slash\"")]
    [InlineData("\"\"", "\"\"")]
    [InlineData("\"AND\"", "\"AND\"")]
    [InlineData("\"-a\"", "\"-a\"")]
    [InlineData("\"ext:pdf\"", "\"ext:pdf\"")]
    [InlineData("\"(x)\"", "\"(x)\"")]
    [InlineData("a\"b c\"", "(and a \"b c\")")]
    [InlineData("\"a\"\"b\"", "(and \"a\" \"b\")")]
    [InlineData("\"multi  space\"", "\"multi  space\"")]
    // --- fields
    [InlineData("name:report", "name:report")]
    [InlineData("NAME:report", "name:report")]
    [InlineData("Ext:PDF", "ext:PDF")]
    [InlineData("ext:pdf", "ext:pdf")]
    [InlineData("ext:jpg;png;gif", "ext:[jpg;png;gif]")]
    [InlineData("ext:\"a b\";c", "ext:[\"a b\";c]")]
    [InlineData("type:image", "type:image")]
    [InlineData("type:image;video", "type:[image;video]")]
    [InlineData("tag:urgent", "tag:urgent")]
    [InlineData("tag:a;b", "tag:[a;b]")]
    [InlineData("attr:hidden", "attr:hidden")]
    [InlineData("attr:!readonly", "attr:!readonly")]
    [InlineData("attr:hidden;system", "attr:[hidden;system]")]
    [InlineData("path:src\\", "path:src\\")]
    [InlineData("path:\"C:\\Program Files\"", "path:\"C:\\Program Files\"")]
    [InlineData("parent:\"C:\\Work\"", "parent:\"C:\\Work\"")]
    [InlineData("parent:C:\\Work", "parent:C:\\Work")]
    [InlineData("content:\"invoice total\"", "content:\"invoice total\"")]
    [InlineData("content:invoice", "content:invoice")]
    [InlineData("category:Photos", "category:Photos")]
    [InlineData("semantic:\"tax documents from last year\"", "semantic:\"tax documents from last year\"")]
    [InlineData("dupe:name", "dupe:name")]
    [InlineData("dupe:sha256", "dupe:sha256")]
    [InlineData("name:a;b", "name:a;b")]
    [InlineData("name:-x", "name:-x")]
    [InlineData("name:a..b", "name:a..b")]
    [InlineData("content:a;b", "content:a;b")]
    [InlineData("(ext:pdf)", "ext:pdf")]
    [InlineData("ext:pdf size:>1MB", "(and ext:pdf size:>1MB)")]
    // --- comparisons and ranges
    [InlineData("size:>50MB", "size:>50MB")]
    [InlineData("size:>=1KB", "size:>=1KB")]
    [InlineData("size:<1GB", "size:<1GB")]
    [InlineData("size:<=10", "size:<=10")]
    [InlineData("size:=0", "size:=0")]
    [InlineData("size:!=0", "size:!=0")]
    [InlineData("size:1KB..2MB", "size:1KB..2MB")]
    [InlineData("size:empty", "size:empty")]
    [InlineData("size:gigantic", "size:gigantic")]
    [InlineData("size:1.5MB", "size:1.5MB")]
    [InlineData("modified:today", "modified:today")]
    [InlineData("modified:>=2026-01-01", "modified:>=2026-01-01")]
    [InlineData("modified:2026-10-01..2026-10-15", "modified:2026-10-01..2026-10-15")]
    [InlineData("modified:last7d", "modified:last7d")]
    [InlineData("created:thisyear", "created:thisyear")]
    [InlineData("accessed:<2025-01-01", "accessed:<2025-01-01")]
    [InlineData("depth:<3", "depth:<3")]
    [InlineData("depth:2..4", "depth:2..4")]
    [InlineData("len:>100", "len:>100")]
    [InlineData("modified:\"2026-01-01\"..\"2026-02-01\"", "modified:\"2026-01-01\"..\"2026-02-01\"")]
    [InlineData("size:>\"5MB\"", "size:>\"5MB\"")]
    [InlineData("modified:>=2026-01-01T08:30", "modified:>=2026-01-01T08:30")]
    // --- regex literals
    [InlineData("regex:/^IMG_\\d{4}\\.jpe?g$/", "regex:/^IMG_\\d{4}\\.jpe?g$/")]
    [InlineData("regex:\"^IMG_\"", "regex:\"^IMG_\"")]
    [InlineData("/^a.*b$/", "/^a.*b$/")]
    [InlineData("/abc/i", "/abc/i")]
    [InlineData("/abc/c", "/abc/c")]
    [InlineData("path:/\\\\bin\\\\(Debug|Release)/", "path:/\\\\bin\\\\(Debug|Release)/")]
    [InlineData("/a\\/b/", "/a/b/")]
    [InlineData("content:/total:\\s*\\$\\d+/", "content:/total:\\s*\\$\\d+/")]
    [InlineData("name:/x y/", "name:/x y/")]
    [InlineData("/(a|b)/ c", "(and /(a|b)/ c)")]
    [InlineData("/a/ | /b/", "(or /a/ /b/)")]
    [InlineData("ext:/x/", "ext:/x/")]
    // --- mixed real-world queries
    [InlineData("ext:pdf content:\"invoice total\"", "(and ext:pdf content:\"invoice total\")")]
    [InlineData("type:image modified:lastweek -path:Thumbs", "(and type:image modified:lastweek (not path:Thumbs))")]
    [InlineData("(ext:jpg | ext:png) size:>2MB", "(and (or ext:jpg ext:png) size:>2MB)")]
    [InlineData("report NOT draft OR final", "(or (and report (not draft)) final)")]
    [InlineData("a&&b", "a&&b")]
    [InlineData("a & b", "(and a b)")]
    [InlineData("a | b", "(or a b)")]
    [InlineData("a|(b)", "(and a| b)")]
    public void Parses(string input, string expected)
    {
        var result = Parser.Parse(input);

        result.Diagnostics.Should().BeEmpty();
        AstFormat.Show(result.Ast).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Empty_queries_have_no_ast_and_no_diagnostics(string input)
    {
        var result = Parser.Parse(input);

        result.Ast.Should().BeNull();
        result.Diagnostics.Should().BeEmpty();
    }

    [Theory]
    [InlineData("\"abc", 0, 4, "Unterminated quoted string")]
    [InlineData("a \"bc", 2, 3, "Unterminated quoted string")]
    [InlineData("/abc", 0, 4, "Unterminated regular expression")]
    [InlineData("x /ab\\/", 2, 5, "Unterminated regular expression")]
    [InlineData("(a b", 0, 1, "Missing ')'")]
    [InlineData("a (b (c)", 2, 1, "Missing ')'")]
    [InlineData("a b)", 3, 1, "Unmatched ')'")]
    [InlineData(")", 0, 1, "Unmatched ')'")]
    [InlineData("ext:", 0, 4, "Missing value for 'ext:'")]
    [InlineData("a ext: b", 2, 4, "Missing value for 'ext:'")]
    [InlineData("size:", 0, 5, "Missing value for 'size:'")]
    [InlineData("(ext:)", 1, 4, "Missing value for 'ext:'")]
    [InlineData("a OR", 2, 2, "Expected an expression after OR")]
    [InlineData("OR a", 0, 2, "Expected an expression before OR")]
    [InlineData("a AND", 2, 3, "Expected an expression after AND")]
    [InlineData("AND a", 0, 3, "Expected an expression before AND")]
    [InlineData("NOT", 0, 3, "Expected an expression after NOT")]
    [InlineData("a NOT", 2, 3, "Expected an expression after NOT")]
    [InlineData("a | | b", 2, 1, "Expected an expression after OR")]
    [InlineData("()", 0, 1, "Empty group")]
    [InlineData("size:>", 5, 1, "Missing value after comparison operator")]
    [InlineData("modified:>= x", 9, 2, "Missing value after comparison operator")]
    [InlineData("size:1KB..", 8, 2, "Missing upper bound of range")]
    [InlineData("ext:pdf;;png", 7, 1, "Empty list item")]
    [InlineData("ext:pdf;", 7, 1, "Empty list item")]
    [InlineData("/abc/x", 5, 1, "Unexpected characters after regular expression")]
    [InlineData("//", 0, 2, "Empty regular expression")]
    [InlineData("name:\"x", 5, 2, "Unterminated quoted string")]
    public void Invalid_inputs_produce_diagnostics_at_the_right_span(string input, int start, int length, string message)
    {
        var result = Parser.Parse(input);

        result.HasErrors.Should().BeTrue();
        result.Diagnostics.Should().Contain(d => d.Message.StartsWith(message, StringComparison.Ordinal) && d.Span == new TextSpan(start, length),
            "diagnostics were: {0}", string.Join("; ", result.Diagnostics.Select(d => $"{d.Message}@{d.Span.Start}+{d.Span.Length}")));
    }

    [Theory]
    [InlineData("(a b", "(and a b)")]
    [InlineData("a b)", "(and a b)")]
    [InlineData("a OR", "a")]
    [InlineData("ext: pdf", "pdf")]
    [InlineData("\"abc", "\"abc\"")]
    [InlineData("a ) b", "(and a b)")]
    public void Recovery_keeps_the_valid_parts(string input, string expected)
    {
        var result = Parser.Parse(input);

        result.HasErrors.Should().BeTrue();
        AstFormat.Show(result.Ast).Should().Be(expected);
    }

    [Fact]
    public void Node_spans_point_into_the_source()
    {
        const string input = "ext:pdf (a | \"b c\")";
        var result = Parser.Parse(input);

        var and = (AndNode)result.Ast!;
        var ext = and.Children[0];
        var or = (OrNode)and.Children[1];
        Slice(input, result.Spans[ext]).Should().Be("ext:pdf");
        Slice(input, result.Spans[or.Children[1]]).Should().Be("\"b c\"");
        Slice(input, result.Spans[or]).Should().Be("a | \"b c\"");
    }

    [Fact]
    public void Ast_serializes_to_json_and_back()
    {
        var ast = Parser.Parse("(ext:jpg;png | /x/i) -size:1KB..2MB modified:>=2026-01-01 \"q3 r\"").Ast!;

        var json = System.Text.Json.JsonSerializer.Serialize(ast);
        var back = System.Text.Json.JsonSerializer.Deserialize<QueryNode>(json);

        back.Should().Be(ast);
    }

    private static string Slice(string s, TextSpan span) => s.Substring(span.Start, span.Length);
}
