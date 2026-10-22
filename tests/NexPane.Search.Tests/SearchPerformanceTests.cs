using System.Diagnostics;
using NexPane.Core.Indexing;
using NexPane.Core.Search;
using NexPane.Search.Execution;
using NexPane.Search.Language;

namespace NexPane.Search.Tests;

[CollectionDefinition(nameof(TimingGroup), DisableParallelization = true)]
public sealed class TimingGroup;

/// <summary>
/// Acceptance checks of P3.22–P3.25 on 1M synthetic entries. Correctness is asserted exactly; timings run alone (no
/// parallel tests) and are reported, with a 2× regression gate here because the default suite shares the machine with
/// other work. The §17 budgets (CI gate 120 %) are measured precisely by tests/NexPane.Benchmarks.
/// </summary>
[Collection(nameof(TimingGroup))]
public sealed class SearchPerformanceTests : IClassFixture<SearchPerformanceTests.MillionEntries>
{
    private const double Gate = 2.0;

    private readonly MillionEntries _data;

    public SearchPerformanceTests(MillionEntries data) => _data = data;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Redos_pattern_runs_in_linear_time_on_one_million_names()
    {
        var plan = Plan("regex:/(a+)+$/");
        plan.Warnings.Should().BeEmpty("NonBacktracking accepts this pattern");
        using var engine = new SearchEngine(_data.Index);

        var times = Measure(5, () => engine.Evaluate(plan, Request(plan.Ast), _data.Index.Current, Ct));
        Report("ReDoS (a+)+$ over 1M names", times, budgetMs: 150);

        times.Min().Should().BeLessThanOrEqualTo(150 * Gate);
    }

    [Fact]
    public async Task Backreference_pattern_uses_the_timed_fallback_and_warns()
    {
        using var engine = new SearchEngine(_data.Index);
        var result = await engine.ExecuteAsync(Request(new QueryParser().Parse("regex:/(\\d)\\1\\1\\1/").Ast!), Ct);

        result.Warnings.Should().Contain(RegexFactory.SlowModeWarning);
        result.Items.Should().NotBeEmpty("names such as report_0000123.txt contain four equal digits");
        var check = new System.Text.RegularExpressions.Regex(@"(\d)\1\1\1");
        result.Items.Should().AllSatisfy(e => check.IsMatch(_data.Index.Generation.GetName(e).ToString()).Should().BeTrue());
    }

    [Fact]
    public void Path_directory_set_matches_brute_force_on_one_million_entries()
    {
        var snapshot = _data.Index.Generation;
        foreach (var needle in new[] { "PROJECT_DIR", "\\SRC_", "DIR1\\", "_DIR12", "D:\\", "\\IMG_00" })
        {
            var predicate = new PathSubstringPredicate(needle);
            predicate.Prepare(snapshot, Ct);
            var segment = snapshot.GetSegment(0);
            var mismatches = 0;
            for (var i = 1; i < snapshot.Count(0); i++)
            {
                var brute = NameArena.Fold(segment.GetFullPath(i)).Contains(needle, StringComparison.Ordinal);
                if (brute != predicate.Match(segment, i))
                {
                    mismatches++;
                }
            }

            mismatches.Should().Be(0, "path:{0} must equal a full-path substring check", needle);
        }

        var plan = Plan("path:src_dir");
        using var engine = new SearchEngine(_data.Index);
        var times = Measure(7, () => engine.Evaluate(plan, Request(plan.Ast), snapshot, Ct));
        Report("path: substring (dir-set) over 1M", times, budgetMs: 50);
        Percentile(times, 0.95).Should().BeLessThanOrEqualTo(50 * Gate);
    }

    [Fact]
    public void Cold_literal_substring_meets_the_budget()
    {
        using var engine = new SearchEngine(_data.Index);
        var queries = new[] { "invoice_00", "report", "IMG_0123", "photo_09", "readme", "zz_nothing", "backup_dir", "notes_0042" };
        var times = new List<double>();
        foreach (var round in Enumerable.Range(0, 5))
        {
            foreach (var q in queries)
            {
                var plan = Plan(q + (round == 0 ? string.Empty : string.Empty));
                var watch = Stopwatch.StartNew();
                engine.Evaluate(plan, Request(plan.Ast), _data.Index.Current, Ct);
                times.Add(watch.Elapsed.TotalMilliseconds);
            }
        }

        times = times.Skip(queries.Length).ToList(); // first round warms the JIT
        Report("Cold literal substring over 1M", times, budgetMs: 15);
        Percentile(times, 0.95).Should().BeLessThanOrEqualTo(15 * Gate);
    }

    [Fact]
    public async Task Incremental_refinement_meets_the_budget_and_returns_the_same_results()
    {
        using var engine = new SearchEngine(_data.Index);
        var parser = new QueryParser();
        var times = new List<double>();
        foreach (var word in new[] { "invoice_0012", "report_00777", "photo_0034", "notes_00123", "readme_0099" })
        {
            SearchRequest? previous = null;
            for (var length = 2; length <= word.Length; length++)
            {
                var request = new SearchRequest(parser.Parse(word[..length]).Ast!, [], SortSpec.Unsorted, RefinementOf: previous);
                var watch = Stopwatch.StartNew();
                var result = await engine.ExecuteAsync(request, Ct);
                watch.Stop();
                if (previous is not null && engine.Plan(request).IsRefinement)
                {
                    times.Add(watch.Elapsed.TotalMilliseconds);
                }

                if (length == word.Length)
                {
                    var cold = await engine.ExecuteAsync(request with { RefinementOf = null }, Ct);
                    result.Items.Should().Equal(cold.Items);
                }

                previous = request;
            }
        }

        times.Should().NotBeEmpty();
        Report("Incremental refinement (keystroke) over 1M", times, budgetMs: 3);
        Percentile(times, 0.50).Should().BeLessThanOrEqualTo(1 * Gate);
        Percentile(times, 0.95).Should().BeLessThanOrEqualTo(3 * Gate);
    }

    [Fact]
    public void Cancelling_a_running_scan_returns_within_5_ms()
    {
        var plan = Plan("path:/q.*w.*e.*r.*t.*y/");
        using var engine = new SearchEngine(_data.Index);
        var latencies = new List<double>();
        for (var i = 0; i < 7; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            using var cts = new CancellationTokenSource();
            var started = new ManualResetEventSlim();
            var task = Task.Run(() =>
            {
                started.Set();
                engine.Evaluate(plan, Request(plan.Ast), _data.Index.Current, cts.Token);
            }, Ct);
            started.Wait(Ct);
            Thread.Sleep(15);
            var watch = Stopwatch.StartNew();
            cts.Cancel();
            var act = () => task.Wait(Ct);
            act.Should().Throw<OperationCanceledException>();
            latencies.Add(watch.Elapsed.TotalMilliseconds);
        }

        Report("Cancellation latency", latencies, budgetMs: 5);
        Percentile(latencies, 0.50).Should().BeLessThanOrEqualTo(5 * Gate);
    }

    private static QueryPlan Plan(string query) => new QueryPlanner(new SearchEngineOptions(), TimeProvider.System).Plan(new QueryParser().Parse(query).Ast!);

    private static SearchRequest Request(QueryNode ast) => new(ast, [], SortSpec.Unsorted);

    private static List<double> Measure(int runs, Action action)
    {
        action(); // warm-up
        var times = new List<double>();
        for (var i = 0; i < runs; i++)
        {
            var watch = Stopwatch.StartNew();
            action();
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        return times;
    }

    private static double Percentile(List<double> values, double p)
    {
        var sorted = values.Order().ToList();
        var index = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private static void Report(string name, List<double> times, double budgetMs)
    {
        TestContext.Current.SendDiagnosticMessage($"{name}: p50 {Percentile(times, 0.5):F2} ms, p95 {Percentile(times, 0.95):F2} ms, min {times.Min():F2} ms (budget {budgetMs} ms)");
        PowerState.SkipTimingGateOnBattery();
    }

    public sealed class MillionEntries
    {
        internal TestIndex Index { get; } = TestIndex.Synthetic(1_000_000);
    }
}
