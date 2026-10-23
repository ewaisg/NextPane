using System.Diagnostics;
using NexPane.Core.Indexing;
using NexPane.Core.Search;
using NexPane.Search.Execution;
using NexPane.Search.Language;

namespace NexPane.Search.Tests;

/// <summary>
/// P3.22 (QA finding, P3.36): the slow regex mode has a per-query budget. The pattern below needs the backtracking
/// engine (backreference) and backtracks catastrophically on names of 30+ "a" followed by "!": every such name hits the
/// 250 ms match timeout. Without a budget, 400 of them cost 400 × 250 ms = 100 s of CPU per evaluation.
/// </summary>
[Collection(nameof(TimingGroup))]
public sealed class RegexBudgetTests
{
    private const string Pathological = "regex:/^(a+)+\\1b/";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Evil(int i) => new string('a', 30 + (i % 5)) + $"!{i}.txt";

    [Fact]
    public async Task A_pattern_that_times_out_on_many_names_aborts_the_query_with_a_diagnostic()
    {
        var index = new TestIndex();
        for (var i = 0; i < 400; i++)
        {
            index.Add($@"evil\{Evil(i)}");
            index.Add($@"plain\aab_{i}.txt");
        }

        index.Publish();
        using var engine = new SearchEngine(index);
        var plan = engine.Plan(Request(Pathological));
        plan.SlowRegexes.Should().ContainSingle().Which.SlowMode.Should().BeTrue();

        var watch = Stopwatch.StartNew();
        var result = await engine.ExecuteAsync(Request(Pathological), Ct);
        watch.Stop();

        TestContext.Current.SendDiagnosticMessage($"aborted after {watch.ElapsedMilliseconds} ms");
        result.Items.Should().BeEmpty("partial results of an aborted regex would be misleading");
        result.Warnings.Should().Contain(RegexFactory.AbortedMessage).And.Contain(RegexFactory.SlowModeWarning);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the first timeout (250 ms per name, one per worker) aborts the scan; 400 × 250 ms without the budget");
    }

    [Fact]
    public void The_total_slow_mode_time_is_capped_even_without_a_single_timeout()
    {
        // Each match is fast enough to stay under 250 ms, but the query keeps going; the 2 s total budget stops it.
        using var engine = new SearchEngine(new TestIndex());
        var plan = engine.Plan(Request(Pathological));
        var regex = plan.SlowRegexes.Single();
        var timeouts = 0;
        var watch = Stopwatch.StartNew();
        var evaluated = 0;
        while (!regex.Aborted && watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            regex.IsMatch(new string('a', 18) + "!", ref timeouts); // a few ms of backtracking each
            evaluated++;
        }

        timeouts.Should().Be(0);
        regex.Aborted.Should().BeTrue();
        plan.RegexAborted.Should().BeTrue();
        regex.SlowModeTime.Should().BeGreaterThanOrEqualTo(RegexFactory.QueryBudget).And.BeLessThan(RegexFactory.QueryBudget + TimeSpan.FromSeconds(1));
        regex.IsMatch("aab", ref timeouts).Should().BeFalse("an aborted regex no longer runs");
        TestContext.Current.SendDiagnosticMessage($"{evaluated} matches before the 2 s budget ran out");
    }

    [Fact]
    public async Task A_live_query_does_not_rerun_an_aborted_pattern_on_later_batches()
    {
        var index = new TestIndex();
        for (var i = 0; i < 64; i++)
        {
            index.Add($@"evil\{Evil(i)}");
        }

        index.Publish();
        using var engine = new SearchEngine(index);
        using var evil = engine.Subscribe(Request(Pathological));
        using var marker = engine.Subscribe(Request("marker_"));

        evil.Current.Warnings.Should().Contain(RegexFactory.AbortedMessage);
        evil.Current.Items.Should().BeEmpty();

        // Resets force a full re-evaluation with a fresh plan; each would cost at least one 250 ms timeout if the
        // aborted pattern were re-run. The marker query (evaluated after it in the same pass) shows when a pass is done.
        var watch = Stopwatch.StartNew();
        for (var batch = 0; batch < 6; batch++)
        {
            var added = new List<IndexChange>
            {
                new(IndexChangeKind.Added, index.Add($@"evil\{Evil(1000 + batch)}")),
                new(IndexChangeKind.Added, index.Add($@"plain\marker_{batch}.txt")),
            };
            index.Publish(added, reset: batch % 2 == 0 ? [0] : null);
            var expected = batch + 1;
            while (marker.Current.Items.Count < expected)
            {
                watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
                await Task.Delay(5, Ct);
            }
        }

        watch.Stop();
        TestContext.Current.SendDiagnosticMessage($"6 batches (3 resets) in {watch.ElapsedMilliseconds} ms");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(750), "3 resets re-running the pattern would cost ≥ 3 × 250 ms");
        evil.Current.Items.Should().BeEmpty();
        evil.Current.Warnings.Should().Contain(RegexFactory.AbortedMessage);
    }

    private static SearchRequest Request(string query) => new(new QueryParser().Parse(query).Ast!, [], SortSpec.Unsorted);
}
