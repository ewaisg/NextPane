using NexPane.Core.Indexing;
using NexPane.Core.Search;
using NexPane.Search.Execution;
using NexPane.Search.Language;

namespace NexPane.Search.Tests;

public sealed class LiveQueryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ten_thousand_creations_produce_deltas_whose_union_equals_a_fresh_query()
    {
        var index = new TestIndex();
        index.Add(@"inbox\existing_report.txt");
        index.Add(@"inbox\unrelated.bin");
        index.Publish();
        using var engine = new SearchEngine(index);
        var request = new SearchRequest(new QueryParser().Parse("report ext:txt").Ast!, [], SortSpec.Unsorted);
        using var live = engine.Subscribe(request);
        var members = new HashSet<EntryRef>();
        var deltas = new List<ResultDelta>();
        using var collect = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var reader = Task.Run(async () =>
        {
            await foreach (var delta in live.Deltas(collect.Token))
            {
                lock (deltas)
                {
                    deltas.Add(delta);
                    if (delta.Reset)
                    {
                        members = [.. delta.Snapshot!.Items];
                    }

                    members.UnionWith(delta.Added);
                    members.ExceptWith(delta.Removed);
                }
            }
        }, Ct);

        var started = DateTime.UtcNow;
        for (var batch = 0; batch < 100; batch++)
        {
            var changes = new List<IndexChange>();
            for (var i = 0; i < 100; i++)
            {
                var n = (batch * 100) + i;
                var name = n % 3 == 0 ? $"report_{n}.txt" : $"other_{n}.txt";
                changes.Add(new IndexChange(IndexChangeKind.Added, index.Add($@"inbox\{name}")));
            }

            index.Publish(changes);
        }

        // Remove and rename a few to exercise Removed/Changed.
        var first = index.AllLive().First(e => index.Generation.GetName(e).StartsWith("report_0", StringComparison.Ordinal));
        index.Remove(first);
        index.Publish([new IndexChange(IndexChangeKind.Removed, first)]);
        var renamed = index.AllLive().First(e => index.Generation.GetName(e).StartsWith("other_1.", StringComparison.Ordinal));
        index.Rename(renamed, "now_a_report.txt");
        index.Publish([new IndexChange(IndexChangeKind.Renamed, renamed)]);

        var fresh = await engine.ExecuteAsync(request, Ct);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            lock (deltas)
            {
                if (members.SetEquals(fresh.Items))
                {
                    break;
                }
            }

            await Task.Delay(20, Ct);
        }

        var elapsed = DateTime.UtcNow - started;
        await collect.CancelAsync();
        await reader.ContinueWith(_ => { }, Ct);

        lock (deltas)
        {
            members.Should().BeEquivalentTo(fresh.Items);
            fresh.Items.Should().HaveCount(3_335);
            deltas.Should().NotBeEmpty();
            var perSecond = deltas.Count / Math.Max(elapsed.TotalSeconds, 0.1);
            deltas.Count.Should().BeLessThanOrEqualTo((int)Math.Ceiling(elapsed.TotalSeconds * 10) + 1,
                "UI-side coalescing allows at most 10 batches/s ({0} batches in {1:F2} s = {2:F1}/s)", deltas.Count, elapsed.TotalSeconds, perSecond);
        }

        live.Current.Items.Should().BeEquivalentTo(fresh.Items);
    }

    [Fact]
    public async Task Compaction_reset_emits_a_reset_delta_with_a_snapshot()
    {
        var index = new TestIndex();
        index.Add("alpha.txt");
        index.Publish();
        using var engine = new SearchEngine(index);
        using var live = engine.Subscribe(new SearchRequest(new QueryParser().Parse("alpha").Ast!, [], SortSpec.Unsorted));
        var deltas = live.Deltas(Ct).GetAsyncEnumerator(Ct);

        (await deltas.MoveNextAsync()).Should().BeTrue();
        deltas.Current.Reset.Should().BeTrue("the first delta carries the initial result");
        deltas.Current.Snapshot!.Items.Should().ContainSingle();

        index.Add("alpha2.txt");
        index.Publish([], reset: [0]);
        (await deltas.MoveNextAsync()).Should().BeTrue();
        deltas.Current.Reset.Should().BeTrue();
        deltas.Current.Snapshot!.Items.Should().HaveCount(2);
        await deltas.DisposeAsync();
    }

    [Fact]
    public async Task Directory_rename_reevaluates_path_dependent_live_queries()
    {
        var index = new TestIndex();
        var dir = index.Add(@"projects\alpha", directory: true);
        index.Add(@"projects\alpha\main.cs");
        index.Add(@"projects\alpha\util.cs");
        index.Publish();
        using var engine = new SearchEngine(index);
        using var live = engine.Subscribe(new SearchRequest(new QueryParser().Parse("path:\\beta\\ ext:cs").Ast!, [], SortSpec.Unsorted));
        live.Current.Items.Should().BeEmpty();

        index.Rename(dir, "beta");
        index.Publish([new IndexChange(IndexChangeKind.Renamed, dir)]);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (live.Current.Items.Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, Ct);
        }

        live.Current.Items.Select(e => index.Generation.GetName(e).ToString()).Should().BeEquivalentTo("main.cs", "util.cs");
    }

    [Fact]
    public void Disposing_unsubscribes()
    {
        var index = new TestIndex();
        using var engine = new SearchEngine(index);
        var live = engine.Subscribe(new SearchRequest(new QueryParser().Parse("x").Ast!, [], SortSpec.Unsorted));
        engine.Hub.ActiveQueries.Should().Be(1);

        live.Dispose();

        engine.Hub.ActiveQueries.Should().Be(0);
    }
}
