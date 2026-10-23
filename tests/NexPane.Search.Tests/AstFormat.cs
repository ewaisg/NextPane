using System.Text;
using NexPane.Core.Search;
using NexPane.Search.Language;

namespace NexPane.Search.Tests;

/// <summary>Compact S-expression rendering of ASTs for readable test expectations.</summary>
internal static class AstFormat
{
    public static string Show(QueryNode? node)
    {
        if (node is null)
        {
            return "<empty>";
        }

        var sb = new StringBuilder();
        Write(sb, node);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, QueryNode node)
    {
        switch (node)
        {
            case AndNode a:
                sb.Append("(and");
                foreach (var c in a.Children)
                {
                    sb.Append(' ');
                    Write(sb, c);
                }

                sb.Append(')');
                break;
            case OrNode o:
                sb.Append("(or");
                foreach (var c in o.Children)
                {
                    sb.Append(' ');
                    Write(sb, c);
                }

                sb.Append(')');
                break;
            case NotNode n:
                sb.Append("(not ");
                Write(sb, n.Operand);
                sb.Append(')');
                break;
            case TermNode t:
                if (t.Field != QueryField.Default)
                {
                    sb.Append(QueryFields.NameOf(t.Field)).Append(':');
                }

                WriteValue(sb, t.Value);
                break;
        }
    }

    private static void WriteValue(StringBuilder sb, QueryValue value)
    {
        switch (value)
        {
            case TextValue t:
                sb.Append(t.Quoted ? "\"" + t.Text + "\"" : t.Text);
                break;
            case RegexValue r:
                sb.Append('/').Append(r.Pattern).Append('/').Append(r.CaseMode switch
                {
                    RegexCaseMode.IgnoreCase => "i",
                    RegexCaseMode.CaseSensitive => "c",
                    _ => string.Empty,
                });
                break;
            case ComparisonValue c:
                sb.Append(c.Operator switch
                {
                    ComparisonOperator.Less => "<",
                    ComparisonOperator.LessOrEqual => "<=",
                    ComparisonOperator.Greater => ">",
                    ComparisonOperator.GreaterOrEqual => ">=",
                    ComparisonOperator.Equal => "=",
                    _ => "!=",
                });
                WriteValue(sb, c.Operand);
                break;
            case RangeValue r:
                WriteValue(sb, r.Low);
                sb.Append("..");
                WriteValue(sb, r.High);
                break;
            case ListValue l:
                sb.Append('[');
                for (var i = 0; i < l.Items.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(';');
                    }

                    WriteValue(sb, l.Items[i]);
                }

                sb.Append(']');
                break;
        }
    }
}
