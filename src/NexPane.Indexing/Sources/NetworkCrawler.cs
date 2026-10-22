using System.Collections.Concurrent;
using NexPane.Core.Indexing;
using NexPane.Core.Vfs;
using NexPane.Indexing.Store;

namespace NexPane.Indexing.Sources;

/// <summary>Options of the network crawler (§7.3).</summary>
public sealed record CrawlerOptions
{
    /// <summary>Maximum concurrent directory listings per host.</summary>
    public int MaxConcurrentListings { get; init; } = 4;

    /// <summary>Re-crawl interval (default 6 h; also crawled on connect).</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(6);

    public int BatchSize { get; init; } = 2048;
}

/// <summary>Result of one crawl.</summary>
public sealed record CrawlResult(long Entries, int DirectoriesListed, int DirectoriesSkipped, int Errors, TimeSpan Elapsed);

/// <summary>
/// Background crawler for UNC shares, mapped drives and other <see cref="IFileSystemProvider"/> locations (§7.3, P3.18):
/// throttled breadth-first listing (at most 4 concurrent listings per host), scheduled re-crawls, and mtime pruning — a
/// directory whose modification time is unchanged since the previous crawl is not listed again; its known children are
/// carried over (sub-directories are still visited). Results are applied as path-addressed deltas and one reconcile.
/// </summary>
public sealed class NetworkCrawler : IAsyncDisposable
{
    private readonly IFileSystemProvider _provider;
    private readonly VfsPath _root;
    private readonly IndexStore _store;
    private readonly SegmentHandle _segment;
    private readonly CrawlerOptions _options;
    private readonly SemaphoreSlim _listings;
    private readonly SemaphoreSlim _crawlGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private ConcurrentDictionary<string, CachedDirectory> _cache = new(StringComparer.OrdinalIgnoreCase);
    private Task? _schedule;
    private int _concurrent;
    private int _maxConcurrent;

    public NetworkCrawler(IFileSystemProvider provider, VfsPath root, IndexStore store, SegmentHandle segment, CrawlerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(segment);
        _provider = provider;
        _root = root;
        _store = store;
        _segment = segment;
        _options = options ?? new CrawlerOptions();
        _listings = new SemaphoreSlim(_options.MaxConcurrentListings, _options.MaxConcurrentListings);
    }

    /// <summary>Highest number of simultaneous listings observed (must not exceed the per-host limit).</summary>
    public int MaxObservedConcurrency => Volatile.Read(ref _maxConcurrent);

    public CrawlResult? LastResult { get; private set; }

    /// <summary>Crawls on connect and then every <see cref="CrawlerOptions.Interval"/> until disposed.</summary>
    public void StartSchedule(TimeProvider? clock = null)
    {
        _schedule ??= Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(_options.Interval, clock ?? TimeProvider.System);
            try
            {
                do
                {
                    try
                    {
                        await CrawlAsync(_stop.Token).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        // Share offline: keep the last index state and retry at the next interval.
                        LastResult = LastResult is null ? null : LastResult with { Errors = LastResult.Errors + 1 };
                    }
                }
                while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                timer.Dispose();
            }
        });
    }

    /// <summary>Runs one crawl now (serialized with scheduled crawls).</summary>
    public async Task<CrawlResult> CrawlAsync(CancellationToken ct)
    {
        await _crawlGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var next = new ConcurrentDictionary<string, CachedDirectory>(StringComparer.OrdinalIgnoreCase);
            var found = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var deltas = new ConcurrentQueue<IndexDelta>();
            long entries = 0;
            var listed = 0;
            var skipped = 0;
            var errors = 0;
            var level = new List<(VfsPath Path, string Relative, long? Modified)> { (_root, string.Empty, null) };
            while (level.Count > 0)
            {
                var nextLevel = new ConcurrentBag<(VfsPath, string, long?)>();
                await Parallel.ForEachAsync(level, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = _options.MaxConcurrentListings * 2 }, async (dir, token) =>
                {
                    var (path, relative, modified) = dir;
                    IReadOnlyList<CachedChild> children;
                    if (modified is long m && _cache.TryGetValue(relative, out var cached) && cached.ModifiedUtc == m)
                    {
                        children = cached.Children; // mtime unchanged: direct children unchanged
                        Interlocked.Increment(ref skipped);
                    }
                    else
                    {
                        try
                        {
                            children = await ListAsync(path, token).ConfigureAwait(false);
                            Interlocked.Increment(ref listed);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            Interlocked.Increment(ref errors);
                            children = _cache.TryGetValue(relative, out var previous) ? previous.Children : [];
                        }

                        foreach (var child in children)
                        {
                            deltas.Enqueue(IndexDelta.UpsertByPath(Combine(relative, child.Name), child.Attributes, child.Size, child.ModifiedUtc, child.CreatedUtc));
                        }
                    }

                    next[relative] = new CachedDirectory(modified ?? 0, children);
                    foreach (var child in children)
                    {
                        var childRelative = Combine(relative, child.Name);
                        found.TryAdd(childRelative, 0);
                        Interlocked.Increment(ref entries);
                        if (!child.IsDirectory)
                        {
                            continue;
                        }

                        var childPath = path.Combine(child.Name);
                        // Directory times come from a stat of the directory itself: a parent listing can carry a stale copy
                        // (NTFS updates the parent index entry lazily), and a reused listing carries last crawl's value.
                        var childModified = await StatModifiedAsync(childPath, token).ConfigureAwait(false) ?? child.ModifiedUtc;

                        nextLevel.Add((childPath, childRelative, childModified));
                    }
                }).ConfigureAwait(false);

                // Parents are applied before the next level is listed, so path-addressed children always resolve.
                await FlushAsync(deltas, ct).ConfigureAwait(false);
                level = [.. nextLevel];
            }

            await _store.SubmitAsync(_segment.Id, [IndexDelta.ReconcileSubtree(string.Empty, new HashSet<string>(found.Keys, StringComparer.OrdinalIgnoreCase))], ct).ConfigureAwait(false);
            await _store.FlushAsync(ct).ConfigureAwait(false);
            _cache = next;
            LastResult = new CrawlResult(entries, listed, skipped, errors, watch.Elapsed);
            return LastResult;
        }
        finally
        {
            _crawlGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_schedule is not null)
        {
            await _schedule.ConfigureAwait(false);
        }

        _stop.Dispose();
        _listings.Dispose();
        _crawlGate.Dispose();
    }

    private async Task<IReadOnlyList<CachedChild>> ListAsync(VfsPath path, CancellationToken ct)
    {
        await _listings.WaitAsync(ct).ConfigureAwait(false);
        var now = Interlocked.Increment(ref _concurrent);
        int observed;
        while (now > (observed = Volatile.Read(ref _maxConcurrent)) && Interlocked.CompareExchange(ref _maxConcurrent, now, observed) != observed)
        {
            // Another listing raised the maximum concurrently; re-read and retry.
            now = Volatile.Read(ref _concurrent);
        }

        try
        {
            var children = new List<CachedChild>();
            await foreach (var entry in _provider.EnumerateAsync(path, new EnumerateOptions(IncludeHidden: true, IncludeSystem: true), ct).ConfigureAwait(false))
            {
                var attributes = (uint)entry.Attributes | (entry.IsContainer && entry.Kind is VfsEntryKind.Directory ? 0x10u : 0u);
                children.Add(new CachedChild(entry.Name, attributes, entry.Size ?? 0, entry.ModifiedUtc?.UtcDateTime.ToFileTimeUtc() ?? 0,
                    entry.CreatedUtc?.UtcDateTime.ToFileTimeUtc() ?? 0, entry.Kind == VfsEntryKind.Directory));
            }

            return children;
        }
        finally
        {
            Interlocked.Decrement(ref _concurrent);
            _listings.Release();
        }
    }

    private async Task<long?> StatModifiedAsync(VfsPath path, CancellationToken ct)
    {
        await _listings.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entry = await _provider.GetEntryAsync(path, ct).ConfigureAwait(false);
            return entry?.ModifiedUtc?.UtcDateTime.ToFileTimeUtc();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            _listings.Release();
        }
    }

    private async Task FlushAsync(ConcurrentQueue<IndexDelta> deltas, CancellationToken ct)
    {
        var batch = new List<IndexDelta>(_options.BatchSize);
        while (deltas.TryDequeue(out var delta))
        {
            batch.Add(delta);
            if (batch.Count == _options.BatchSize)
            {
                await _store.SubmitAsync(_segment.Id, batch, ct).ConfigureAwait(false);
                batch = new List<IndexDelta>(_options.BatchSize);
            }
        }

        await _store.SubmitAsync(_segment.Id, batch, ct).ConfigureAwait(false);
        await _store.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string Combine(string relative, string name) => relative.Length == 0 ? name : relative + "\\" + name;

    private sealed record CachedChild(string Name, uint Attributes, long Size, long ModifiedUtc, long CreatedUtc, bool IsDirectory);

    private sealed record CachedDirectory(long ModifiedUtc, IReadOnlyList<CachedChild> Children);
}
