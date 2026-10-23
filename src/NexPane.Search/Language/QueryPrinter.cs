using System.Globalization;
using System.Text;
using NexPane.Core.Search;

namespace NexPane.Search.Language;

/// <summary>
/// Prints the canonical text of an AST (§8.3): AND as juxtaposition, <c>OR</c> and <c>NOT</c> as keywords, parentheses
/// only where precedence or nesting requires them, so <c>Parse(Print(ast)) == ast</c>. Unquoted text that would not
/// re-parse as the same bare token is printed quoted.
/// </summary>
internal sealed class QueryPrinter : IQueryPrinter
{
    public string Print(QueryNode ast)
    {
        ArgumentNullException.ThrowIfNull(ast);
        var sb = new StringBuilder();
        Write(sb, ast, Context.Top);
        return sb.ToString();
    }

    private enum Context
    {
        Top,
        OrOperand,
        AndOperand,
        NotOperand,
    }

    private static void Write(StringBuilder sb, QueryNode node, Context context)
    {
        switch (node)
        {
            case OrNode or:
            {
                var parens = context is Context.OrOperand or Context.AndOperand or Context.NotOperand;
                if (parens)
                {
                    sb.Append('(');
                }

                for (var i = 0; i < or.Children.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(" OR ");
                    }

                    Write(sb, or.Children[i], Context.OrOperand);
                }

                if (parens)
                {
                    sb.Append(')');
                }

                break;
            }

            case AndNode and:
            {
                var parens = context is Context.AndOperand or Context.NotOperand;
                if (parens)
                {
                    sb.Append('(');
                }

                for (var i = 0; i < and.Children.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(' ');
                    }

                    Write(sb, and.Children[i], Context.AndOperand);
                }

                if (parens)
                {
                    sb.Append(')');
                }

                break;
            }

            case NotNode not:
                sb.Append("NOT ");
                Write(sb, not.Operand, Context.NotOperand);
                break;

            case TermNode term:
                WriteTerm(sb, term);
                break;

            default:
                throw new ArgumentException($"Unknown query node type '{node.GetType().Name}'.", nameof(node));
        }
    }

    private static void WriteTerm(StringBuilder sb, TermNode term)
    {
        var field = term.Field;
        if (field != QueryField.Default)
        {
            sb.Append(QueryFields.NameOf(field)).Append(':');
        }

        switch (term.Value)
        {
            case RegexValue regex:
                WriteRegex(sb, regex);
                break;
            case ComparisonValue cmp:
                sb.Append(cmp.Operator switch
                {
                    ComparisonOperator.Less => "<",
                    ComparisonOperator.LessOrEqual => "<=",
                    ComparisonOperator.Greater => ">",
                    ComparisonOperator.GreaterOrEqual => ">=",
                    ComparisonOperator.Equal => "=",
                    ComparisonOperator.NotEqual => "!=",
                    _ => throw new ArgumentOutOfRangeException(nameof(term), cmp.Operator, "Unknown operator."),
                });
                WriteScalar(sb, cmp.Operand, field, ScalarPosition.Comparison);
                break;
            case RangeValue range:
                WriteScalar(sb, range.Low, field, ScalarPosition.Structured);
                sb.Append("..");
                WriteScalar(sb, range.High, field, ScalarPosition.Structured);
                break;
            case ListValue list:
                for (var i = 0; i < list.Items.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(';');
                    }

                    WriteScalar(sb, list.Items[i], field, ScalarPosition.Structured);
                }

                break;
            case TextValue text:
                WriteScalar(sb, text, field, ScalarPosition.Single);
                break;
            default:
                throw new ArgumentException($"Unknown query value type '{term.Value.GetType().Name}'.", nameof(term));
        }
    }

    private enum ScalarPosition
    {
        Single,
        Comparison,
        Structured,
    }

    private static void WriteScalar(StringBuilder sb, TextValue value, QueryField field, ScalarPosition position)
    {
        if (!value.Quoted && IsSafeBare(value.Text, field, position))
        {
            sb.Append(value.Text);
            return;
        }

        sb.Append('"');
        var text = value.Text;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                sb.Append("\\\"");
            }
            else if (c == '\\' && (i + 1 == text.Length || text[i + 1] == '"' || text[i + 1] == '\\'))
            {
                sb.Append("\\\\");
            }
            else
            {
                sb.Append(c);
            }
        }

        sb.Append('"');
    }

    /// <summary>True when <paramref name="text"/> re-lexes as exactly this unquoted scalar.</summary>
    public static bool IsSafeBare(string text, QueryField field, bool structuredContext) =>
        IsSafeBare(text, field, structuredContext ? ScalarPosition.Structured : ScalarPosition.Single);

    private static bool IsSafeBare(string text, QueryField field, ScalarPosition position)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || c is '(' or ')' or '"')
            {
                return false;
            }
        }

        var structured = field != QueryField.Default && (QueryFields.AllowsComparison(field) || QueryFields.AllowsList(field));
        if (((structured && position != ScalarPosition.Comparison) || position == ScalarPosition.Structured) &&(text.Contains(';', StringComparison.Ordinal) || text.Contains("..", StringComparison.Ordinal) || text.EndsWith('.')))
        {
            return false;
        }

        var first = text[0];
        if (field == QueryField.Default)
        {
            // Must not look like an operator, a NOT prefix, a regex literal or a field prefix.
            if (text is "AND" or "OR" or "NOT" or "|" or "&")
            {
                return false;
            }

            if ((first is '-' or '!') && text.Length > 1)
            {
                return false;
            }

            if (first == '/')
            {
                return false;
            }

            var colon = text.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && QueryFields.TryGet(text.AsSpan(0, colon), out _) && IsAsciiLetters(text.AsSpan(0, colon)))
            {
                return false;
            }

            return true;
        }

        if (first == '/' && QueryFields.AllowsRegex(field))
        {
            return false;
        }

        if (position != ScalarPosition.Comparison && QueryFields.AllowsComparison(field) && first is '<' or '>' or '=' or '!')
        {
            return false;
        }

        if (position == ScalarPosition.Comparison && first == '=')
        {
            return false; // "<" + "=x" would re-lex as "<=" + "x"
        }

        return true;
    }

    private static bool IsAsciiLetters(ReadOnlySpan<char> span)
    {
        foreach (var c in span)
        {
            if (!char.IsAsciiLetter(c))
            {
                return false;
            }
        }

        return true;
    }

    private static void WriteRegex(StringBuilder sb, RegexValue regex)
    {
        sb.Append('/');
        var pattern = regex.Pattern;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length)
            {
                // Keep escape pairs intact; an escaped slash in the stored pattern was unescaped by the lexer.
                if (pattern[i + 1] == '/')
                {
                    sb.Append('\\').Append("\\/");
                }
                else
                {
                    sb.Append(c).Append(pattern[i + 1]);
                }

                i++;
                continue;
            }

            sb.Append(c == '/' ? "\\/" : c.ToString(CultureInfo.InvariantCulture));
        }

        sb.Append('/');
        if (regex.CaseMode == RegexCaseMode.IgnoreCase)
        {
            sb.Append('i');
        }
        else if (regex.CaseMode == RegexCaseMode.CaseSensitive)
        {
            sb.Append('c');
        }
    }
}
