using BenchmarkDotNet.Attributes;
using NexPane.Core.Indexing;
using NexPane.Core.Search;
using NexPane.Search.Execution;
using NexPane.Search.Language;

namespace NexPane.Benchmarks;

/// <summary>§17 filename-search budgets on synthetic 1M-entry indexes (budgets listed in README.md).</summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 15)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet owns the lifecycle: [GlobalCleanup] disposes.")]
public class SearchBenchmarks
{
    private readonly QueryParser _parser = new();
    private SyntheticIndex _index = null!;
    private SearchEngine _engine = null!;
    private QueryPlan _cold = null!;
    private QueryPlan _redos = null!;
    private QueryPlan _path = null!;
    private SearchRequest _previous = null!;
    private SearchRequest _refined = null!;
    private SearchRequest _broadPrevious = null!;
    private SearchRequest _broadRefined = null!;

    [GlobalSetup]
    public void Setup()
    {
        _index = SyntheticIndex.Build(1_000_000);
        _engine = new SearchEngine(_index);
        var planner = new QueryPlanner(new SearchEngineOptions(), TimeProvider.System);
        _cold = planner.Plan(_parser.Parse("invoice_00").Ast!);
        _redos = planner.Plan(_parser.Parse("regex:/(a+)+$/").Ast!);
        _path = planner.Plan(_parser.Parse("path:src_dir").Ast!);
        _previous = new SearchRequest(_parser.Parse("photo_0").Ast!, [], SortSpec.Unsorted);
        _engine.ExecuteAsync(_previous, CancellationToken.None).GetAwaiter().GetResult();
        _refined = new SearchRequest(_parser.Parse("photo_00").Ast!, [], SortSpec.Unsorted, RefinementOf: _previous);
        _broadPrevious = new SearchRequest(_parser.Parse("in").Ast!, [], SortSpec.Unsorted);
        _broadRefined = new SearchRequest(_parser.Parse("inv").Ast!, [], SortSpec.Unsorted, RefinementOf: _broadPrevious);
    }

    [GlobalCleanup]
    public void Cleanup() => _engine.Dispose();

    /// <summary>Budget p50 ≤ 5 ms, p95 ≤ 15 ms.</summary>
    [Benchmark]
    public int ColdLiteralSubstring1M() => _engine.Evaluate(_cold, Request(_cold), _index.Current, CancellationToken.None).Count;

    /// <summary>Budget p50 ≤ 1 ms, p95 ≤ 3 ms (keystroke "photo_0" → "photo_00").</summary>
    [Benchmark]
    public int IncrementalRefinement1M() => _engine.ExecuteAsync(_refined, CancellationToken.None).GetAwaiter().GetResult().Items.Count;

    /// <summary>Broad keystroke "in" → "inv" (≈167k candidates → ≈83k results); same budget as above.</summary>
    [Benchmark]
    public int BroadRefinement1M()
    {
        _engine.ExecuteAsync(_broadPrevious, CancellationToken.None).GetAwaiter().GetResult();
        return _engine.ExecuteAsync(_broadRefined, CancellationToken.None).GetAwaiter().GetResult().Items.Count;
    }

    /// <summary>The cold "in" query alone (subtract from <see cref="BroadRefinement1M"/> for the refinement cost).</summary>
    [Benchmark]
    public int BroadColdBaseline1M() => _engine.ExecuteAsync(_broadPrevious, CancellationToken.None).GetAwaiter().GetResult().Items.Count;

    /// <summary>Budget p95 ≤ 150 ms (NonBacktracking).</summary>
    [Benchmark]
    public int RegexRedos1M() => _engine.Evaluate(_redos, Request(_redos), _index.Current, CancellationToken.None).Count;

    /// <summary>Budget ≤ 50 ms.</summary>
    [Benchmark]
    public int PathDirectorySet1M() => _engine.Evaluate(_path, Request(_path), _index.Current, CancellationToken.None).Count;

    private static SearchRequest Request(QueryPlan plan) => new(plan.Ast, [], SortSpec.Unsorted);
}

/// <summary>§17 "cold literal substring, 5M entries" budget (p95 ≤ 60 ms).</summary>
[SimpleJob(warmupCount: 2, iterationCount: 10)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet owns the lifecycle: [GlobalCleanup] disposes.")]
public class LargeSearchBenchmarks
{
    private SyntheticIndex _index = null!;
    private SearchEngine _engine = null!;
    private QueryPlan _cold = null!;

    [GlobalSetup]
    public void Setup()
    {
        _index = SyntheticIndex.Build(5_000_000);
        _engine = new SearchEngine(_index);
        _cold = new QueryPlanner(new SearchEngineOptions(), TimeProvider.System).Plan(new QueryParser().Parse("invoice_00").Ast!);
    }

    [GlobalCleanup]
    public void Cleanup() => _engine.Dispose();

    [Benchmark]
    public int ColdLiteralSubstring5M() => _engine.Evaluate(_cold, new SearchRequest(_cold.Ast, [], SortSpec.Unsorted), _index.Current, CancellationToken.None).Count;
}

/// <summary>P3.07: 1M <c>GetFullPath</c> calls (budget ≤ 400 ms per million).</summary>
[SimpleJob(warmupCount: 2, iterationCount: 10)]
public class PathReconstructionBenchmarks
{
    private SyntheticIndex _index = null!;

    [GlobalSetup]
    public void Setup() => _index = SyntheticIndex.Build(1_000_000);

    [Benchmark]
    public long GetFullPath1M()
    {
        var segment = _index.Generation.GetSegment(0);
        long total = 0;
        for (var i = 1; i <= 1_000_000; i++)
        {
            total += segment.GetFullPath(i).Length;
        }

        return total;
    }

    [Benchmark]
    public int Snapshot()
    {
        var generation = _index.Generation;
        return generation.Count(0) + (int)(generation.Get(new EntryRef(0, 1)).Size & 1);
    }
}

/// <summary>
/// Broad keystroke refinement in isolation: the cold base query ("in", ≈167k results) runs in the iteration setup, the
/// measured invocation is the next keystroke ("inv"). Budget: p50 ≤ 1 ms, p95 ≤ 3 ms (§17).
/// </summary>
[SimpleJob(warmupCount: 5, iterationCount: 60, invocationCount: 1)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet owns the lifecycle: [GlobalCleanup] disposes.")]
public class BroadRefinementBenchmarks
{
    private SyntheticIndex _index = null!;
    private SearchEngine _engine = null!;
    private SearchRequest _base = null!;
    private SearchRequest _next = null!;

    [Params("in|inv", "re|rep", "invoice_0|invoice_00")]
    public string Keystroke { get; set; } = "in|inv";

    [GlobalSetup]
    public void Setup()
    {
        _index = SyntheticIndex.Build(1_000_000);
        _engine = new SearchEngine(_index);
    }

    [IterationSetup]
    public void TypeBase()
    {
        var parser = new QueryParser();
        var parts = Keystroke.Split('|');
        _base = new SearchRequest(parser.Parse(parts[0]).Ast!, [], SortSpec.Unsorted);
        _next = new SearchRequest(parser.Parse(parts[1]).Ast!, [], SortSpec.Unsorted, RefinementOf: _base);
        _engine.ExecuteAsync(_base, CancellationToken.None).GetAwaiter().GetResult();
    }

    [Benchmark]
    public int NextKeystroke() => _engine.ExecuteAsync(_next, CancellationToken.None).GetAwaiter().GetResult().Items.Count;

    [GlobalCleanup]
    public void Cleanup() => _engine.Dispose();
}
