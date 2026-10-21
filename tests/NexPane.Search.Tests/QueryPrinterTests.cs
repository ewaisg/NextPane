using System.Text;
using NexPane.Core.Search;
using NexPane.Search.Language;

namespace NexPane.Search.Tests;

public sealed class QueryPrinterTests
{
    private static readonly QueryParser Parser = new();
    private static readonly QueryPrinter Printer = new();

    [Theory]
    [InlineData("a b | c", "a b OR c")]
    [InlineData("a | b c", "a OR b c")]
    [InlineData("(a | b) c", "(a OR b) c")]
    [InlineData("(a b) c", "(a b) c")]
    [InlineData("a (b c)", "a (b c)")]
    [InlineData("a | (b | c)", "a OR (b OR c)")]
    [InlineData("-a", "NOT a")]
    [InlineData("!(a b)", "NOT (a b)")]
    [InlineData("NOT (a | b)", "NOT (a OR b)")]
    [InlineData("--a", "NOT NOT a")]
    [InlineData("a & b", "a b")]
    [InlineData("EXT:pdf", "ext:pdf")]
    [InlineData("ext:jpg;png", "ext:jpg;png")]
    [InlineData("size:>=50MB", "size:>=50MB")]
    [InlineData("size:1KB..2MB", "size:1KB..2MB")]
    [InlineData("/a\\/b/i", "/a\\/b/i")]
    [InlineData("\"q3 report\"", "\"q3 report\"")]
    [InlineData("\"a\\\"b\"", "\"a\\\"b\"")]
    [InlineData("\"C:\\Work\\\\\"", "\"C:\\Work\\\\\"")]
    public void Prints_canonical_text(string input, string expected)
    {
        var ast = Parser.Parse(input).Ast!;

        Printer.Print(ast).Should().Be(expected);
    }

    [Fact]
    public void Unsafe_unquoted_text_is_printed_quoted()
    {
        Printer.Print(new TermNode(QueryField.Default, new TextValue("AND"))).Should().Be("\"AND\"");
        Printer.Print(new TermNode(QueryField.Default, new TextValue("ext:pdf"))).Should().Be("\"ext:pdf\"");
        Printer.Print(new TermNode(QueryField.Default, new TextValue("-x"))).Should().Be("\"-x\"");
        Printer.Print(new TermNode(QueryField.Default, new TextValue("a b"))).Should().Be("\"a b\"");
        Printer.Print(new TermNode(QueryField.Size, new TextValue(">5"))).Should().Be("size:\">5\"");
        Printer.Print(new TermNode(QueryField.Ext, new TextValue("a;b"))).Should().Be("ext:\"a;b\"");
        Printer.Print(new TermNode(QueryField.Path, new TextValue("/x"))).Should().Be("path:\"/x\"");
    }

    [Fact]
    public void Ten_thousand_random_asts_round_trip_through_print_and_parse()
    {
        var random = new Random(20261021);
        for (var i = 0; i < 10_000; i++)
        {
            var ast = RandomNode(random, depth: 0);
            var printed = Printer.Print(ast);
            var reparsed = Parser.Parse(printed);

            reparsed.Diagnostics.Should().BeEmpty("'{0}' must parse cleanly", printed);
            reparsed.Ast.Should().Be(ast, "Parse(Print(ast)) == ast for '{0}' (got {1}, expected {2})",
                printed, AstFormat.Show(reparsed.Ast), AstFormat.Show(ast));
        }
    }

    private static readonly QueryField[] Fields = Enum.GetValues<QueryField>();
    private const string BareAlphabet = "abcXYZ019_-.*?\\:é日!|&;<>=/";
    private const string QuotedAlphabet = "ab Z9 _-()\"\\:;.*/|&!";
    private const string RegexAlphabet = "ab^$.*+?()[]{}|\\d /";

    private static QueryNode RandomNode(Random r, int depth)
    {
        var roll = r.Next(10);
        if (depth >= 4 || roll < 5)
        {
            return RandomTerm(r);
        }

        if (roll < 7)
        {
            return new AndNode(new NodeList(Enumerable.Range(0, r.Next(2, 5)).Select(_ => RandomNode(r, depth + 1))));
        }

        if (roll < 9)
        {
            return new OrNode(new NodeList(Enumerable.Range(0, r.Next(2, 5)).Select(_ => RandomNode(r, depth + 1))));
        }

        return new NotNode(RandomNode(r, depth + 1));
    }

    private static TermNode RandomTerm(Random r)
    {
        var field = Fields[r.Next(Fields.Length)];
        QueryValue value;
        var pick = r.Next(10);
        if (QueryFields.AllowsRegex(field) && pick < 3)
        {
            value = new RegexValue(RandomRegex(r), (RegexCaseMode)r.Next(3));
        }
        else if (QueryFields.AllowsComparison(field) && pick < 5)
        {
            value = new ComparisonValue((ComparisonOperator)r.Next(6), RandomText(r, field, structured: false, comparison: true));
        }
        else if (QueryFields.AllowsComparison(field) && pick < 7)
        {
            value = new RangeValue(RandomText(r, field, structured: true), RandomText(r, field, structured: true));
        }
        else if (QueryFields.AllowsList(field) && pick < 7)
        {
            value = new ListValue(new TextList(Enumerable.Range(0, r.Next(2, 5)).Select(_ => RandomText(r, field, structured: true))));
        }
        else
        {
            value = RandomText(r, field, structured: false);
        }

        return new TermNode(field, value);
    }

    private static TextValue RandomText(Random r, QueryField field, bool structured, bool comparison = false)
    {
        if (r.Next(3) == 0)
        {
            return new TextValue(RandomString(r, QuotedAlphabet, 0, 8), Quoted: true);
        }

        var text = RandomString(r, BareAlphabet, 1, 8);
        var safe = comparison
            ? !text.Any(c => char.IsWhiteSpace(c) || c is '(' or ')' or '"') && text[0] != '='
            : QueryPrinter.IsSafeBare(text, field, structured);
        return new TextValue(text, Quoted: !safe);
    }

    private static string RandomRegex(Random r)
    {
        while (true)
        {
            var pattern = RandomString(r, RegexAlphabet, 1, 10);
            // The lexer turns "\/" into "/", and a trailing lone backslash would escape the closing slash.
            var valid = true;
            for (var i = 0; i < pattern.Length; i++)
            {
                if (pattern[i] == '\\')
                {
                    if (i + 1 >= pattern.Length || pattern[i + 1] == '/')
                    {
                        valid = false;
                        break;
                    }

                    i++;
                }
            }

            if (valid)
            {
                return pattern;
            }
        }
    }

    private static string RandomString(Random r, string alphabet, int min, int max)
    {
        var length = r.Next(min, max + 1);
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            sb.Append(alphabet[r.Next(alphabet.Length)]);
        }

        return sb.ToString();
    }
}
