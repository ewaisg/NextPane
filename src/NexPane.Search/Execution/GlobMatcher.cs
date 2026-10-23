namespace NexPane.Search.Execution;

/// <summary>Span-based <c>*</c>/<c>?</c> glob matcher over case-folded names (§7.2: no regex). Whole-name match.</summary>
public sealed class GlobMatcher
{
    private readonly string _pattern;

    /// <param name="foldedPattern">Pattern already folded with <c>NameArena.Fold</c>.</param>
    public GlobMatcher(string foldedPattern)
    {
        ArgumentNullException.ThrowIfNull(foldedPattern);
        _pattern = foldedPattern;
        // Longest literal run (between wildcards) is used as a cheap vectorized pre-check.
        var best = string.Empty;
        foreach (var part in foldedPattern.Split('*', '?'))
        {
            if (part.Length > best.Length)
            {
                best = part;
            }
        }

        RequiredLiteral = best;
    }

    public static bool HasWildcards(string text) => text.AsSpan().IndexOfAny('*', '?') >= 0;

    public string RequiredLiteral { get; }

    public bool IsMatch(ReadOnlySpan<char> foldedName)
    {
        if (RequiredLiteral.Length > 0 && foldedName.IndexOf(RequiredLiteral, StringComparison.Ordinal) < 0)
        {
            return false;
        }

        return Match(_pattern, foldedName);
    }

    /// <summary>Iterative wildcard match with single backtrack point (linear for typical patterns).</summary>
    public static bool Match(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t]))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
