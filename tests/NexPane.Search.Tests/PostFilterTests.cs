using System.Diagnostics;
using NexPane.Core.Indexing;
using NexPane.Core.Search;
using NexPane.Search.Execution;
using NexPane.Search.Language;

namespace NexPane.Search.Tests;

/// <summary><c>dupe:</c>, <c>content:/regex/</c> and <c>accessed:</c> post-filters over real files in a temp tree.</summary>
public sealed class PostFilterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexpane-post-" + Guid.NewGuid().ToString("N"));
    private readonly TestIndex _index;

    public PostFilterTests()
    {
        Directory.CreateDirectory(_root);
        _index = new TestIndex(_root + "\\");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Dupe_sha256_hashes_only_size_collision_groups()
    {
        AddFile(@"a\photo.jpg", "same-content-1");
        AddFile(@"b\photo-copy.jpg", "same-content-1");
        AddFile(@"c\photo.jpg", "diff-content-1"); // same size, different bytes
        AddFile(@"d\unique-size.jpg", "this one has a unique length");
        AddFile(@"e\other.jpg", "xyz");
        _index.Publish();
        using var engine = new SearchEngine(_index);

        var result = await engine.ExecuteAsync(Request("ext:jpg dupe:sha256"), Ct);

        Names(result).Should().BeEquivalentTo("photo.jpg", "photo-copy.jpg");
        Paths(result).Should().NotContain(p => p.Contains(@"\c\", StringComparison.Ordinal));
        engine.HashesComputed.Should().Be(3, "only the three files of the 14-byte size group are hashed");
    }

    [Fact]
    public async Task Dupe_name_and_size_group_without_hashing()
    {
        AddFile(@"a\readme.md", "1");
        AddFile(@"b\README.md", "22");
        AddFile(@"c\notes.md", "22");
        AddFile(@"d\solo.md", "4444");
        _index.Publish();
        using var engine = new SearchEngine(_index);

        Names(await engine.ExecuteAsync(Request("ext:md dupe:name"), Ct)).Should().BeEquivalentTo("readme.md", "README.md");
        Names(await engine.ExecuteAsync(Request("ext:md dupe:size"), Ct)).Should().BeEquivalentTo("README.md", "notes.md");
        Names(await engine.ExecuteAsync(Request("ext:md dupe:name;size"), Ct)).Should().BeEmpty();
        engine.HashesComputed.Should().Be(0);
    }

    [Fact]
    public async Task Content_regex_scans_candidates_and_returns_matches()
    {
        AddFile("invoice1.txt", "Item A\nTotal: $120\n");
        AddFile("invoice2.txt", "Item B\nTotal: none\n");
        AddFile("invoice3.log", "Total: $5");
        AddFile("binary.txt", "Total: $9\0\0\0");
        _index.Publish();
        using var engine = new SearchEngine(_index);

        var result = await engine.ExecuteAsync(Request("ext:txt content:/total:\\s*\\$\\d+/"), Ct);

        Names(result).Should().BeEquivalentTo("invoice1.txt");
        engine.ContentFilesScanned.Should().Be(3, "only ext:txt candidates are read");
    }

    [Fact]
    public async Task Content_regex_reports_progress_and_the_cap()
    {
        for (var i = 0; i < 30; i++)
        {
            AddFile($"f{i:D2}.txt", i % 2 == 0 ? "needle here" : "haystack");
        }

        _index.Publish();
        using var engine = new SearchEngine(_index, new SearchEngineOptions { ContentRegexMaxFiles = 10 });

        var results = new List<SearchResult>();
        await foreach (var partial in engine.ExecuteProgressiveAsync(Request("content:/needle/ ext:txt"), Ct))
        {
            results.Add(partial);
        }

        var final = results[^1];
        final.Items.Should().HaveCount(5);
        final.Warnings.Should().Contain(w => w.Contains("cap reached", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Content_regex_cancels_within_50_ms()
    {
        var big = string.Concat(Enumerable.Repeat("lorem dolor sit amet consectetur ", 30_000));
        for (var i = 0; i < 400; i++)
        {
            AddFile($"big{i:D3}.txt", big);
        }

        _index.Publish();
        using var engine = new SearchEngine(_index);
        using var cts = new CancellationTokenSource();
        var run = Task.Run(async () =>
        {
            await foreach (var partial in engine.ExecuteProgressiveAsync(Request("ext:txt content:/zzz_never_found/"), cts.Token))
            {
                _ = partial;
            }
        }, Ct);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (engine.ContentFilesScanned < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(1, Ct);
        }

        var watch = Stopwatch.StartNew();
        await cts.CancelAsync();
        var act = async () => await run;
        await act.Should().ThrowAsync<OperationCanceledException>();
        watch.Stop();

        engine.ContentFilesScanned.Should().BeLessThan(400);
        watch.ElapsedMilliseconds.Should().BeLessThanOrEqualTo(50);
    }

    [Fact]
    public async Task Accessed_filter_stats_candidates()
    {
        AddFile("recent.txt", "x");
        AddFile("old.txt", "y");
        File.SetLastAccessTimeUtc(Path.Combine(_root, "old.txt"), new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastAccessTimeUtc(Path.Combine(_root, "recent.txt"), DateTime.UtcNow);
        _index.Publish();
        using var engine = new SearchEngine(_index);

        Names(await engine.ExecuteAsync(Request("ext:txt accessed:<2010-01-01"), Ct)).Should().BeEquivalentTo("old.txt");
        Names(await engine.ExecuteAsync(Request("ext:txt accessed:today"), Ct)).Should().BeEquivalentTo("recent.txt");
    }

    private void AddFile(string relative, string content)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        _index.Add(relative, size: new FileInfo(full).Length);
    }

    private static SearchRequest Request(string query) => new(new QueryParser().Parse(query).Ast!, [], SortSpec.Unsorted);

    private List<string> Names(SearchResult result) => result.Items.Select(e => _index.Generation.GetName(e).ToString()).ToList();

    private List<string> Paths(SearchResult result) => result.Items.Select(e => _index.Generation.GetFullPath(e)).ToList();
}
