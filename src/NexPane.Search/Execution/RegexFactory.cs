using System.Diagnostics;
using System.Text.RegularExpressions;
using NexPane.Core.Search;

namespace NexPane.Search.Execution;

/// <summary>
/// A compiled user regex and whether it needed the backtracking fallback. Slow-mode regexes carry a per-query budget
/// (§7.2, §21 2026-10-22): the first match that hits the 250 ms timeout, or more than <see cref="RegexFactory.QueryBudget"/>
/// of matching plus 250 ms per <see cref="RegexFactory.BatchSize"/> names, aborts the regex. After that every match returns false at once and the engine replaces the
/// result with <see cref="RegexFactory.AbortedMessage"/>, so a live query never re-runs the pathological pattern.
/// </summary>
public sealed record UserRegex(Regex Regex, bool SlowMode)
{
    private static readonly long BudgetTicks = (long)(RegexFactory.QueryBudget.TotalSeconds * Stopwatch.Frequency);
    private static readonly long BatchTicks = (long)(RegexFactory.FallbackTimeout.TotalSeconds * Stopwatch.Frequency);
    private static readonly long FlushTicks = Stopwatch.Frequency / 100; // 10 ms

    // Per-thread accumulation (flushed every 1024 matches or 10 ms) keeps the shared counters uncontended in the
    // parallel scan; shared Interlocked updates per match would cost more than the matches they measure.
    [ThreadStatic]
    private static UserRegex? t_owner;

    [ThreadStatic]
    private static long t_ticks;

    [ThreadStatic]
    private static int t_matches;

    private long _slowTicks;
    private long _slowMatches;
    private int _aborted;

    /// <summary>True once the slow-mode budget of this query is spent.</summary>
    public bool Aborted => Volatile.Read(ref _aborted) != 0;

    /// <summary>Time spent matching in slow mode, as flushed by the scan threads (diagnostics).</summary>
    public TimeSpan SlowModeTime => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _slowTicks));

    /// <summary>Matches with the timeout of the fallback engine; a timeout counts as no match and is reported.</summary>
    public bool IsMatch(ReadOnlySpan<char> input, ref int timeouts)
    {
        if (!SlowMode)
        {
            return Regex.IsMatch(input); // NonBacktracking: linear time, no timeout
        }

        if (Aborted)
        {
            return false;
        }

        var start = Stopwatch.GetTimestamp();
        try
        {
            return Regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            Interlocked.Increment(ref timeouts);
            Volatile.Write(ref _aborted, 1);
            return false;
        }
        finally
        {
            Account(Stopwatch.GetTimestamp() - start);
        }
    }

    private void Account(long elapsed)
    {
        if (!ReferenceEquals(t_owner, this))
        {
            t_owner?.Flush();
            t_owner = this;
        }

        t_ticks += elapsed;
        if (++t_matches >= 1024 || t_ticks >= FlushTicks)
        {
            Flush();
        }
    }

    private void Flush()
    {
        // Allowance: QueryBudget, plus 250 ms (the per-name timeout) for every entry batch of BatchSize names matched.
        var matches = Interlocked.Add(ref _slowMatches, t_matches);
        var ticks = Interlocked.Add(ref _slowTicks, t_ticks);
        t_matches = 0;
        t_ticks = 0;
        if (ticks > BudgetTicks + (BatchTicks * (matches / RegexFactory.BatchSize)))
        {
            Volatile.Write(ref _aborted, 1);
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

    /// <summary>Slow-mode allowance of one query: <see cref="QueryBudget"/> plus <see cref="FallbackTimeout"/> per
    /// <see cref="BatchSize"/> names matched (the "250 ms per entry batch" of §7.2), summed over all scan threads.</summary>
    public static readonly TimeSpan QueryBudget = TimeSpan.FromSeconds(2);

    public const int BatchSize = 4_096;

    public const string AbortedMessage = "Regex stopped: the pattern needs the backtracking engine and exceeded its time budget (one name took over 250 ms, or the query used up its time allowance of 2 s plus 250 ms per 4,096 names). No results are shown; simplify the pattern (avoid nested quantifiers combined with backreferences or lookarounds).";

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
