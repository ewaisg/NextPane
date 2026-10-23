using System.Collections.Concurrent;
using System.Threading.Channels;
using NexPane.Core.Indexing;
using NexPane.Indexing.Store;
using NexPane.Shell.FileSystem;

namespace NexPane.Indexing.Sources;

/// <summary>Options of the parallel walker.</summary>
public sealed record WalkerOptions(
    bool FollowReparsePoints = false,
    int MaxDegreeOfParallelism = 0,
    int BatchSize = 4096,
    IReadOnlyCollection<string>? ExcludedPaths = null);

/// <summary>Outcome of one walk.</summary>
public sealed record WalkResult(long Entries, long Directories, long Errors, TimeSpan Elapsed)
{
    public double EntriesPerSecond => Elapsed.TotalSeconds > 0 ? Entries / Elapsed.TotalSeconds : Entries;
}

/// <summary>
/// Parallel directory walker (§7.3, P3.15): a shared work queue of directories processed by
/// <c>ProcessorCount × 2</c> workers with <c>FindFirstFileExW(FindExInfoBasic, LARGE_FETCH)</c> on <c>\\?\</c> paths.
/// Reparse points are listed but not descended unless configured. Fresh walks emit id-addressed upserts with
/// walker-assigned synthetic ids; catch-up walks over an existing segment emit path-addressed upserts followed by a
/// reconcile, so unchanged entries keep their ids.
/// </summary>
internal sealed class WalkerVolumeSource(string rootPath, WalkerOptions? options = null)
{
    /// <summary>Walker-assigned ids live in their own range, below the store's synthetic range (2^62).</summary>
    private const ulong WalkerIdBase = 1UL << 61;
    private readonly WalkerOptions _options = options ?? new WalkerOptions();
    private long _nextId = (long)WalkerIdBase;

    public string RootPath { get; } = rootPath.EndsWith('\\') ? rootPath : rootPath + "\\";

    /// <summary>Full walk of a freshly registered (or reset) segment.</summary>
    public Task<WalkResult> WalkFreshAsync(IndexStore store, SegmentHandle segment, IProgress<IndexProgress>? progress, CancellationToken ct) =>
        WalkAsync(store, segment, relativeRoot: string.Empty, fresh: true, progress, ct);

    /// <summary>Walks <paramref name="relativeRoot"/> (empty = whole volume) with path-addressed upserts and removes
    /// entries below it that no longer exist (catch-up after startup, watcher overflow).</summary>
    public Task<WalkResult> RewalkAsync(IndexStore store, SegmentHandle segment, string relativeRoot, IProgress<IndexProgress>? progress, CancellationToken ct) =>
        WalkAsync(store, segment, relativeRoot.Trim('\\'), fresh: false, progress, ct);

    private async Task<WalkResult> WalkAsync(IndexStore store, SegmentHandle segment, string relativeRoot, bool fresh, IProgress<IndexProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(segment);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var excluded = new HashSet<string>((_options.ExcludedPaths ?? []).Select(p => p.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
        var queue = Channel.CreateUnbounded<DirectoryWork>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
        var found = fresh ? null : new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        long outstanding = 1;
        long entries = 0;
        long directories = 0;
        long errors = 0;
        var rootDirectory = relativeRoot.Length == 0 ? RootPath : RootPath + relativeRoot;
        queue.Writer.TryWrite(new DirectoryWork(rootDirectory, relativeRoot, segment.RootFileId));
        var workers = _options.MaxDegreeOfParallelism > 0 ? _options.MaxDegreeOfParallelism : Environment.ProcessorCount * 2;

        async Task WorkerAsync()
        {
            var batch = new List<IndexDelta>(_options.BatchSize);
            var listing = new List<NativeEntry>(256);
            var deferred = new List<DirectoryWork>();
            await foreach (var work in queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    listing.Clear();
                    var error = NativeDirectory.Enumerate(work.FullPath, listing);
                    if (error != 0)
                    {
                        Interlocked.Increment(ref errors);
                    }

                    foreach (var entry in listing)
                    {
                        var relative = work.RelativePath.Length == 0 ? entry.Name : work.RelativePath + "\\" + entry.Name;
                        var full = work.FullPath.EndsWith('\\') ? work.FullPath + entry.Name : work.FullPath + "\\" + entry.Name;
                        if (excluded.Contains(full))
                        {
                            continue;
                        }

                        var id = fresh ? (ulong)Interlocked.Increment(ref _nextId) : 0;
                        batch.Add(fresh
                            ? IndexDelta.UpsertById(id, work.ParentId, entry.Name, (uint)entry.Attributes, entry.Size, entry.ModifiedUtc, entry.CreatedUtc)
                            : IndexDelta.UpsertByPath(relative, (uint)entry.Attributes, entry.Size, entry.ModifiedUtc, entry.CreatedUtc));
                        found?.TryAdd(relative, 0);
                        Interlocked.Increment(ref entries);
                        if (entry.IsDirectory && (!entry.IsReparsePoint || _options.FollowReparsePoints))
                        {
                            Interlocked.Increment(ref directories);
                            Interlocked.Increment(ref outstanding);
                            if (fresh)
                            {
                                queue.Writer.TryWrite(new DirectoryWork(full, relative, id));
                            }
                            else
                            {
                                // Path-addressed children must reach the store after their parent: enqueue after submitting.
                                deferred.Add(new DirectoryWork(full, relative, id));
                            }
                        }

                        if (batch.Count >= _options.BatchSize)
                        {
                            await store.SubmitAsync(segment.Id, batch, ct).ConfigureAwait(false);
                            batch = new List<IndexDelta>(_options.BatchSize);
                        }
                    }

                    if (deferred.Count > 0)
                    {
                        if (batch.Count > 0)
                        {
                            await store.SubmitAsync(segment.Id, batch, ct).ConfigureAwait(false);
                            batch = new List<IndexDelta>(_options.BatchSize);
                        }

                        foreach (var child in deferred)
                        {
                            queue.Writer.TryWrite(child);
                        }

                        deferred.Clear();
                    }
                }
                finally
                {
                    if (Interlocked.Decrement(ref outstanding) == 0)
                    {
                        queue.Writer.TryComplete();
                    }
                }

                if (queue.Reader.Count == 0 && batch.Count > 0)
                {
                    // Idle moment: publish what this worker has so partial results become searchable early.
                    await store.SubmitAsync(segment.Id, batch, ct).ConfigureAwait(false);
                    batch = new List<IndexDelta>(_options.BatchSize);
                    progress?.Report(new IndexProgress(RootPath, VolumeScanState.Scanning, Interlocked.Read(ref entries)));
                }
            }

            if (batch.Count > 0)
            {
                await store.SubmitAsync(segment.Id, batch, ct).ConfigureAwait(false);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(WorkerAsync, ct))).ConfigureAwait(false);
        if (found is not null)
        {
            await store.SubmitAsync(segment.Id, [IndexDelta.ReconcileSubtree(relativeRoot, new HashSet<string>(found.Keys, StringComparer.OrdinalIgnoreCase))], ct).ConfigureAwait(false);
        }

        await store.FlushAsync(ct).ConfigureAwait(false);
        var result = new WalkResult(entries, directories, errors, started.Elapsed);
        progress?.Report(new IndexProgress(RootPath, VolumeScanState.Complete, entries));
        return result;
    }

    private sealed record DirectoryWork(string FullPath, string RelativePath, ulong ParentId);
}
