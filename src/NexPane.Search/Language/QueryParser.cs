using System.Text;
using NexPane.Core.Search;

namespace NexPane.Search.Language;

/// <summary>
/// Recursive-descent parser for the query grammar (§8.1) with error recovery and diagnostics carrying spans.
/// <para>Precedence: <c>NOT</c> (also <c>-</c>/<c>!</c> prefixes) binds tightest, then AND (juxtaposition, <c>AND</c>,
/// standalone <c>&amp;</c>), then OR (<c>OR</c>, standalone <c>|</c>). Keywords are case-sensitive upper case.
/// <c>|</c> and <c>&amp;</c> are operators only as standalone tokens, so names such as <c>R&amp;D.docx</c> stay searchable.</para>
/// <para>Quoted strings support <c>\"</c> and <c>\\</c> escapes (any other backslash is literal). Regex literals support
/// <c>\/</c>. Values of unknown fields (<c>C:\x</c>, <c>foo:bar</c>) are plain text.</para>
/// </summary>
public sealed class QueryParser : IQueryParser
{
    public ParseResult Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var state = new State(text);
        state.Tokens = Lex(state);
        QueryNode? ast = null;
        if (state.Tokens.Count > 0)
        {
            var parts = new List<QueryNode>();
            while (state.Position < state.Tokens.Count)
            {
                var node = ParseOr(state);
                if (node is not null)
                {
                    parts.Add(node);
                }

                if (state.Position < state.Tokens.Count)
                {
                    var stray = state.Tokens[state.Position];
                    state.Error(stray.Kind == TokenKind.RParen ? "Unmatched ')'." : $"Unexpected '{stray.Text(text)}'.", stray.Span);
                    state.Position++;
                }
            }

            ast = parts.Count switch
            {
                0 => null,
                1 => parts[0],
                _ => state.Track(new AndNode(new NodeList(parts)), new TextSpan(0, text.Length)),
            };
        }

        return new ParseResult(ast, state.Diagnostics, state.Spans);
    }

    // ====================================================================== parser

    private static QueryNode? ParseOr(State s)
    {
        var start = s.Peek?.Span.Start ?? s.Text.Length;
        var first = ParseAnd(s);
        var operands = new List<QueryNode>();
        if (first is not null)
        {
            operands.Add(first);
        }

        while (s.Peek is { Kind: TokenKind.Or } op)
        {
            s.Position++;
            var right = ParseAnd(s);
            if (right is null)
            {
                s.Error("Expected an expression after OR.", op.Span);
                continue;
            }

            if (operands.Count == 0)
            {
                s.Error("Expected an expression before OR.", op.Span);
            }

            operands.Add(right);
        }

        return operands.Count switch
        {
            0 => null,
            1 => operands[0],
            _ => s.Track(new OrNode(new NodeList(operands)), new TextSpan(start, s.LastEnd - start)),
        };
    }

    private static QueryNode? ParseAnd(State s)
    {
        var start = s.Peek?.Span.Start ?? s.Text.Length;
        var operands = new List<QueryNode>();
        while (true)
        {
            var token = s.Peek;
            if (token is null)
            {
                break;
            }

            if (token.Kind == TokenKind.And)
            {
                s.Position++;
                if (operands.Count == 0)
                {
                    s.Error("Expected an expression before AND.", token.Span);
                }

                var right = ParseUnary(s);
                if (right is null)
                {
                    s.Error("Expected an expression after AND.", token.Span);
                    continue;
                }

                operands.Add(right);
                continue;
            }

            if (token.Kind is TokenKind.Term or TokenKind.LParen or TokenKind.Not)
            {
                var operand = ParseUnary(s);
                if (operand is not null)
                {
                    operands.Add(operand);
                }

                continue;
            }

            break; // OR, ')' or end
        }

        return operands.Count switch
        {
            0 => null,
            1 => operands[0],
            _ => s.Track(new AndNode(new NodeList(operands)), new TextSpan(start, s.LastEnd - start)),
        };
    }

    private static QueryNode? ParseUnary(State s)
    {
        var token = s.Peek;
        if (token is { Kind: TokenKind.Not })
        {
            s.Position++;
            var operand = ParseUnary(s);
            if (operand is null)
            {
                s.Error("Expected an expression after NOT.", token.Span);
                return null;
            }

            return s.Track(new NotNode(operand), new TextSpan(token.Span.Start, s.LastEnd - token.Span.Start));
        }

        return ParsePrimary(s);
    }

    private static QueryNode? ParsePrimary(State s)
    {
        var token = s.Peek;
        if (token is null)
        {
            return null;
        }

        if (token.Kind == TokenKind.LParen)
        {
            s.Position++;
            var inner = ParseOr(s);
            if (s.Peek is { Kind: TokenKind.RParen })
            {
                s.Position++;
            }
            else
            {
                s.Error("Missing ')'.", token.Span);
            }

            if (inner is null)
            {
                s.Error("Empty group.", token.Span);
            }

            return inner;
        }

        if (token.Kind == TokenKind.Term)
        {
            s.Position++;
            return token.Node is null ? null : s.Track(token.Node, token.Span);
        }

        return null;
    }

    // ====================================================================== lexer

    private static List<Token> Lex(State s)
    {
        var text = s.Text;
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '(')
            {
                tokens.Add(new Token(TokenKind.LParen, new TextSpan(i, 1)));
                i++;
                continue;
            }

            if (c == ')')
            {
                tokens.Add(new Token(TokenKind.RParen, new TextSpan(i, 1)));
                i++;
                continue;
            }

            if ((c == '-' || c == '!') && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]) && text[i + 1] != ')')
            {
                tokens.Add(new Token(TokenKind.Not, new TextSpan(i, 1)));
                i++;
                continue;
            }

            if ((c == '|' || c == '&') && IsStandalone(text, i, 1))
            {
                tokens.Add(new Token(c == '|' ? TokenKind.Or : TokenKind.And, new TextSpan(i, 1)));
                i++;
                continue;
            }

            foreach (var (keyword, kind) in Keywords)
            {
                if (string.CompareOrdinal(text, i, keyword, 0, keyword.Length) == 0 && IsStandalone(text, i, keyword.Length))
                {
                    tokens.Add(new Token(kind, new TextSpan(i, keyword.Length)));
                    i += keyword.Length;
                    goto next;
                }
            }

            tokens.Add(LexTerm(s, ref i));
        next:;
        }

        return tokens;
    }

    private static readonly (string Keyword, TokenKind Kind)[] Keywords =
    [
        ("AND", TokenKind.And),
        ("OR", TokenKind.Or),
        ("NOT", TokenKind.Not),
    ];

    private static bool IsStandalone(string text, int start, int length)
    {
        var end = start + length;
        return end >= text.Length || char.IsWhiteSpace(text[end]) || text[end] == '(' || text[end] == ')';
    }

    private static Token LexTerm(State s, ref int i)
    {
        var text = s.Text;
        var start = i;
        var field = QueryField.Default;

        // field:value
        var j = i;
        while (j < text.Length && char.IsAsciiLetter(text[j]))
        {
            j++;
        }

        if (j > i && j < text.Length && text[j] == ':' && QueryFields.TryGet(text.AsSpan(i, j - i), out var known))
        {
            field = known;
            i = j + 1;
            if (i >= text.Length || char.IsWhiteSpace(text[i]) || text[i] == ')' || text[i] == '(')
            {
                s.Error($"Missing value for '{QueryFields.NameOf(field)}:'.", new TextSpan(start, i - start));
                return new Token(TokenKind.Term, new TextSpan(start, i - start));
            }
        }

        var value = LexValue(s, field, ref i, start);
        var span = new TextSpan(start, i - start);
        return new Token(TokenKind.Term, span) { Node = value is null ? null : new TermNode(field, value) };
    }

    private static QueryValue? LexValue(State s, QueryField field, ref int i, int termStart)
    {
        var text = s.Text;
        var c = text[i];
        if (c == '/' && QueryFields.AllowsRegex(field))
        {
            return LexRegex(s, ref i);
        }

        if (field != QueryField.Default && QueryFields.AllowsComparison(field) && c is '<' or '>' or '=' or '!')
        {
            var opStart = i;
            ComparisonOperator op;
            if (c == '<' && Next(text, i) == '=')
            {
                op = ComparisonOperator.LessOrEqual;
                i += 2;
            }
            else if (c == '>' && Next(text, i) == '=')
            {
                op = ComparisonOperator.GreaterOrEqual;
                i += 2;
            }
            else if (c == '!' && Next(text, i) == '=')
            {
                op = ComparisonOperator.NotEqual;
                i += 2;
            }
            else if (c == '<')
            {
                op = ComparisonOperator.Less;
                i++;
            }
            else if (c == '>')
            {
                op = ComparisonOperator.Greater;
                i++;
            }
            else if (c == '=')
            {
                op = ComparisonOperator.Equal;
                i++;
            }
            else
            {
                return LexScalarForm(s, field, ref i);
            }

            var operand = LexScalar(s, field, ref i, stopAtRangeAndList: false);
            if (operand is null)
            {
                s.Error("Missing value after comparison operator.", new TextSpan(opStart, i - opStart));
                return null;
            }

            return new ComparisonValue(op, operand);
        }

        return LexScalarForm(s, field, ref i);
    }

    /// <summary>scalar | scalar ".." scalar | scalar { ";" scalar }.</summary>
    private static QueryValue? LexScalarForm(State s, QueryField field, ref int i)
    {
        var text = s.Text;
        var structured = field != QueryField.Default && (QueryFields.AllowsComparison(field) || QueryFields.AllowsList(field));
        var first = LexScalar(s, field, ref i, structured);
        if (first is null)
        {
            return null;
        }

        if (structured && QueryFields.AllowsComparison(field) && i + 1 < text.Length && text[i] == '.' && text[i + 1] == '.')
        {
            var dots = i;
            i += 2;
            var high = LexScalar(s, field, ref i, stopAtRangeAndList: true);
            if (high is null)
            {
                s.Error("Missing upper bound of range.", new TextSpan(dots, 2));
                return first;
            }

            return new RangeValue(first, high);
        }

        if (structured && QueryFields.AllowsList(field) && i < text.Length && text[i] == ';')
        {
            var items = new List<TextValue> { first };
            while (i < text.Length && text[i] == ';')
            {
                var sep = i;
                i++;
                var item = LexScalar(s, field, ref i, stopAtRangeAndList: true);
                if (item is null)
                {
                    s.Error("Empty list item.", new TextSpan(sep, 1));
                    continue;
                }

                items.Add(item);
            }

            return items.Count == 1 ? first : new ListValue(new TextList(items));
        }

        return first;
    }

    private static TextValue? LexScalar(State s, QueryField field, ref int i, bool stopAtRangeAndList)
    {
        var text = s.Text;
        if (i >= text.Length)
        {
            return null;
        }

        if (text[i] == '"')
        {
            return LexQuoted(s, ref i);
        }

        var start = i;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || c is '(' or ')' or '"')
            {
                break;
            }

            if (stopAtRangeAndList && (c == ';' || (c == '.' && i + 1 < text.Length && text[i + 1] == '.')))
            {
                break;
            }

            i++;
        }

        return i == start ? null : new TextValue(text[start..i]);
    }

    private static TextValue LexQuoted(State s, ref int i)
    {
        var text = s.Text;
        var start = i;
        i++; // opening quote
        var sb = new StringBuilder();
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && (text[i + 1] == '"' || text[i + 1] == '\\'))
            {
                sb.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '"')
            {
                i++;
                return new TextValue(sb.ToString(), Quoted: true);
            }

            sb.Append(c);
            i++;
        }

        s.Error("Unterminated quoted string.", new TextSpan(start, i - start));
        return new TextValue(sb.ToString(), Quoted: true);
    }

    private static RegexValue LexRegex(State s, ref int i)
    {
        var text = s.Text;
        var start = i;
        i++; // opening slash
        var sb = new StringBuilder();
        var closed = false;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                if (text[i + 1] == '/')
                {
                    sb.Append('/');
                }
                else
                {
                    sb.Append(c).Append(text[i + 1]);
                }

                i += 2;
                continue;
            }

            if (c == '/')
            {
                i++;
                closed = true;
                break;
            }

            sb.Append(c);
            i++;
        }

        if (!closed)
        {
            s.Error("Unterminated regular expression.", new TextSpan(start, i - start));
            return new RegexValue(sb.ToString());
        }

        var mode = RegexCaseMode.Default;
        if (i < text.Length && (text[i] == 'i' || text[i] == 'c'))
        {
            mode = text[i] == 'i' ? RegexCaseMode.IgnoreCase : RegexCaseMode.CaseSensitive;
            i++;
        }

        if (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != ')' && text[i] != '(')
        {
            var junk = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != ')' && text[i] != '(')
            {
                i++;
            }

            s.Error("Unexpected characters after regular expression (allowed flags: i, c).", new TextSpan(junk, i - junk));
        }

        if (sb.Length == 0)
        {
            s.Error("Empty regular expression.", new TextSpan(start, i - start));
        }

        return new RegexValue(sb.ToString(), mode);
    }

    private static char Next(string text, int i) => i + 1 < text.Length ? text[i + 1] : '\0';

    private enum TokenKind
    {
        LParen,
        RParen,
        And,
        Or,
        Not,
        Term,
    }

    private sealed record Token(TokenKind Kind, TextSpan Span)
    {
        public QueryNode? Node { get; init; }

        public string Text(string source) => source.Substring(Span.Start, Span.Length);
    }

    private sealed class State(string text)
    {
        public string Text { get; } = text;

        public List<Token> Tokens { get; set; } = [];

        public int Position { get; set; }

        public List<QueryDiagnostic> Diagnostics { get; } = [];

        public Dictionary<QueryNode, TextSpan> Spans { get; } = new(ReferenceEqualityComparer.Instance);

        public Token? Peek => Position < Tokens.Count ? Tokens[Position] : null;

        public int LastEnd => Position > 0 ? Tokens[Position - 1].Span.End : 0;

        public void Error(string message, TextSpan span) => Diagnostics.Add(new QueryDiagnostic(DiagnosticSeverity.Error, message, span));

        public T Track<T>(T node, TextSpan span)
            where T : QueryNode
        {
            Spans.TryAdd(node, span);
            return node;
        }
    }
}
