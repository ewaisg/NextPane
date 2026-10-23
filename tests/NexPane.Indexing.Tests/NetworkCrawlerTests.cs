using System.Diagnostics;
using System.Runtime.CompilerServices;
using NexPane.Core.Indexing;
using NexPane.Core.Vfs;
using NexPane.Indexing.Sources;
using NexPane.Indexing.Store;
using NexPane.Shell.FileSystem;

namespace NexPane.Indexing.Tests;

public sealed class NetworkCrawlerTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexpane-crawl-" + Guid.NewGuid().ToString("N"));
    private IndexStore _store = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _store = new IndexStore();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Crawls_detects_changed_subtrees_and_skips_unchanged_directories()
    {
        for (var d = 0; d < 20; d++)
        {
            var dir = Directory.CreateDirectory(Path.Combine(_root, $"dir{d:D2}", "inner"));
            File.WriteAllText(Path.Combine(dir.FullName, "file.txt"), "x");
            File.WriteAllText(Path.Combine(_root, $"dir{d:D2}", "top.txt"), "y");
        }

        var counting = new CountingProvider(new LocalFileSystemProvider());
        await using var crawler = await CreateAsync(counting);

        var first = await crawler.CrawlAsync(Ct);
        first.Entries.Should().Be(80);
        first.DirectoriesListed.Should().Be(41, "the root, 20 dirs and 20 inner dirs are listed the first time");
        Names().Should().Contain(["file.txt", "top.txt", "inner"]);

        // Change one subtree: add a file in dir05 (its mtime changes) and delete one in dir07\inner.
        File.WriteAllText(Path.Combine(_root, "dir05", "added.txt"), "new");
        File.Delete(Path.Combine(_root, "dir07", "inner", "file.txt"));
        var second = await crawler.CrawlAsync(Ct);

        second.DirectoriesSkipped.Should().BeGreaterThan(30, "unchanged directories are not listed again");
        second.DirectoriesListed.Should().BeLessThan(10);
        Paths().Should().Contain(Path.Combine(_root, "dir05", "added.txt"));
        Paths().Should().NotContain(Path.Combine(_root, "dir07", "inner", "file.txt"));
        Paths().Should().Contain(Path.Combine(_root, "dir08", "inner", "file.txt"));
        crawler.MaxObservedConcurrency.Should().BeLessThanOrEqualTo(4);
        counting.MaxConcurrent.Should().BeLessThanOrEqualTo(4);
        counting.MaxConcurrent.Should().BeGreaterThan(1, "listings run in parallel up to the limit");
    }

    [Fact]
    public async Task Scheduled_crawl_runs_on_start_and_repeats()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        await using var crawler = await CreateAsync(new LocalFileSystemProvider(), TimeSpan.FromMilliseconds(200));
        crawler.StartSchedule();

        await WaitFor(() => Names().Contains("a.txt"));
        File.WriteAllText(Path.Combine(_root, "b.txt"), "b");
        await WaitFor(() => Names().Contains("b.txt"));
    }

    [Fact]
    [Trait("Category", "Elevated")]
    public async Task Crawls_a_real_smb_share_on_localhost()
    {
        var share = "NexPaneTest" + Guid.NewGuid().ToString("N")[..8];
        Run($"New-SmbShare -Name {share} -Path '{_root}' -FullAccess $env:USERNAME | Out-Null");
        try
        {
            File.WriteAllText(Path.Combine(_root, "over-smb.txt"), "s");
            var unc = VfsPath.FromWindowsPath($@"\\localhost\{share}");
            var segment = await _store.RegisterVolumeAsync(new IndexVolumeInfo($@"\\localhost\{share}", VolumeKind.Smb), 1, Ct);
            await using var crawler = new NetworkCrawler(new LocalFileSystemProvider(), unc, _store, segment);
            (await crawler.CrawlAsync(Ct)).Entries.Should().Be(1);
            File.WriteAllText(Path.Combine(_root, "second.txt"), "t");
            (await crawler.CrawlAsync(Ct)).Entries.Should().Be(2);
        }
        finally
        {
            Run($"Remove-SmbShare -Name {share} -Force");
        }
    }

    private async Task<NetworkCrawler> CreateAsync(IFileSystemProvider provider, TimeSpan? interval = null)
    {
        var segment = await _store.RegisterVolumeAsync(new IndexVolumeInfo(_root + "\\", VolumeKind.Smb), 1, Ct);
        return new NetworkCrawler(provider, VfsPath.FromWindowsPath(_root), _store, segment, new CrawlerOptions { Interval = interval ?? TimeSpan.FromHours(6) });
    }

    private List<string> Names()
    {
        var g = _store.CurrentGeneration;
        var s = g.GetSegment(0);
        return Enumerable.Range(1, g.Count(0) - 1).Where(i => !s.IsDeleted(i)).Select(i => s.GetName(i).ToString()).ToList();
    }

    private List<string> Paths()
    {
        var g = _store.CurrentGeneration;
        var s = g.GetSegment(0);
        return Enumerable.Range(1, g.Count(0) - 1).Where(i => !s.IsDeleted(i)).Select(s.GetFullPath).ToList();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }

        condition().Should().BeTrue();
    }

    private static void Run(string command)
    {
        using var process = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"{command}\"") { UseShellExecute = false, CreateNoWindow = true })!;
        process.WaitForExit();
        process.ExitCode.Should().Be(0);
    }

    /// <summary>Wraps the real provider and records the highest number of simultaneous listings.</summary>
    private sealed class CountingProvider(IFileSystemProvider inner) : IFileSystemProvider
    {
        private int _current;
        private int _max;

        public int MaxConcurrent => Volatile.Read(ref _max);

        public string Scheme => inner.Scheme;

        public VfsCapabilities GetCapabilities(VfsPath root) => inner.GetCapabilities(root);

        public async IAsyncEnumerable<VfsEntry> EnumerateAsync(VfsPath directory, EnumerateOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _current);
            InterlockedMax(ref _max, now);
            try
            {
                await Task.Delay(20, ct); // widen the window so concurrent listings overlap measurably
                await foreach (var e in inner.EnumerateAsync(directory, options, ct))
                {
                    yield return e;
                }
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }

        public ValueTask<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken ct) => inner.GetEntryAsync(path, ct);

        public ValueTask<Stream> OpenReadAsync(VfsPath path, CancellationToken ct) => inner.OpenReadAsync(path, ct);

        public ValueTask<Stream> OpenWriteAsync(VfsPath path, WriteMode mode, CancellationToken ct) => inner.OpenWriteAsync(path, mode, ct);

        public ValueTask CreateDirectoryAsync(VfsPath path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);

        public ValueTask DeleteAsync(VfsPath path, DeleteOptions options, CancellationToken ct) => inner.DeleteAsync(path, options, ct);

        public ValueTask MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken ct) => inner.MoveAsync(source, destination, overwrite, ct);

        public ValueTask SetTimesAsync(VfsPath path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct) => inner.SetTimesAsync(path, created, modified, ct);

        public IVfsWatcher? CreateWatcher(VfsPath directory, bool recursive) => inner.CreateWatcher(directory, recursive);

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
            {
                value = Math.Max(value, Volatile.Read(ref target));
            }
        }
    }
}
