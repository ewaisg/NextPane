using System.Text.RegularExpressions;
using NexPane.Core.Search;

namespace NexPane.Search.Execution;

/// <summary>A compiled user regex and whether it needed the backtracking fallback.</summary>
public sealed record UserRegex(Regex Regex, bool SlowMode)
{
    /// <summary>Matches with the timeout of the fallback engine; a timeout counts as no match and is reported.</summary>
    public bool IsMatch(ReadOnlySpan<char> input, ref int timeouts)
    {
        try
        {
            return Regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            Interlocked.Increment(ref timeouts);
            return false;
        }
    }
}

/// <summary>
/// User regexes (§7.2, §16): <c>RegexOptions.NonBacktracking | CultureInvariant</c> (linear time, ReDoS-safe), plus
/// <c>IgnoreCase</c> unless the literal has the <c>c</c> flag. Patterns NonBacktracking rejects (backreferences,
/// lookarounds, atomic groups) fall back to the backtracking engine with a 250 ms match timeout ("slow regex mode").
/// </summary>
public static class RegexFactory
{
    public static readonly TimeSpan FallbackTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Why the most recent pattern on this thread needed the fallback engine (diagnostics).</summary>
    [ThreadStatic]
    private static string? _fallbackReason;

    public static string? FallbackReason
    {
        get => _fallbackReason;
        private set => _fallbackReason = value;
    }

    public const string SlowModeWarning = "Slow regex mode: the pattern uses constructs (backreferences, lookarounds or atomic groups) that need the backtracking engine; matches time out after 250 ms.";

    public static bool TryCreate(string pattern, RegexCaseMode caseMode, out UserRegex? regex, out string? error)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var options = RegexOptions.CultureInvariant;
        if (caseMode != RegexCaseMode.CaseSensitive)
        {
            options |= RegexOptions.IgnoreCase;
        }

        try
        {
            regex = new UserRegex(new Regex(pattern, options | RegexOptions.NonBacktracking), SlowMode: false);
            error = null;
            return true;
        }
        catch (NotSupportedException ex)
        {
            // Constructs unsupported by NonBacktracking: use the timed backtracking engine below.
            FallbackReason = ex.Message;
        }
        catch (ArgumentException ex)
        {
            regex = null;
            error = "Invalid regular expression: " + ex.Message;
            return false;
        }

        try
        {
            regex = new UserRegex(new Regex(pattern, options, FallbackTimeout), SlowMode: true);
            error = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            regex = null;
            error = "Invalid regular expression: " + ex.Message;
            return false;
        }
    }
}
