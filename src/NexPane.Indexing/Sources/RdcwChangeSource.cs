using NexPane.Core.Indexing;
using NexPane.Indexing.Store;
using NexPane.Shell.FileSystem;

namespace NexPane.Indexing.Sources;

/// <summary>
/// Keeps a walker-indexed volume (or folder) live (§7.4, P3.16): consumes <see cref="RdcwWatcher"/> notifications,
/// turns them into path-addressed index deltas (one store batch per drained burst), re-walks new or moved-in
/// directories, and on buffer overflow re-walks the whole watched subtree with a reconcile.
/// </summary>
public sealed class RdcwChangeSource : IAsyncDisposable
{
    private readonly IndexStore _store;
    private readonly SegmentHandle _segment;
    private readonly WalkerVolumeSource _walker;
    private readonly RdcwWatcher _watcher;
    private readonly string _root;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public RdcwChangeSource(IndexStore store, SegmentHandle segment, WalkerVolumeSource walker, int bufferSize = RdcwWatcher.DefaultBufferSize)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(walker);
        _store = store;
        _segment = segment;
        _walker = walker;
        _root = walker.RootPath;
        _watcher = new RdcwWatcher(_root, recursive: true, bufferSize);
    }

    /// <summary>Overflow-triggered re-walks completed (diagnostics/tests).</summary>
    public int OverflowRewalks { get; private set; }

    /// <summary>Notifications applied.</summary>
    public long EventsApplied { get; private set; }

    /// <summary>True when disposal interrupted a wait for events (normal shutdown).</summary>
    public bool StoppedWhileWaiting { get; private set; }

    /// <summary>Raised after each applied burst (tests wait on it).</summary>
    public event EventHandler? BatchApplied;

    public void Start()
    {
        _watcher.Start();
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _watcher.Dispose();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                StoppedWhileWaiting = true;
            }
        }

        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var reader = _watcher.Events;
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            var deltas = new List<IndexDelta>();
            var rewalk = new List<string>();
            var overflow = false;
            string? pendingOld = null;
            var count = 0;
            while (reader.TryRead(out var e))
            {
                count++;
                switch (e.Action)
                {
                    case RdcwAction.Overflow:
                        overflow = true;
                        break;
                    case RdcwAction.Removed:
                        deltas.Add(IndexDelta.RemoveByPath(e.RelativePath));
                        break;
                    case RdcwAction.Added:
                    case RdcwAction.Modified:
                        if (Stat(e, out var upsert, out var isDirectory))
                        {
                            deltas.Add(upsert);
                            if (isDirectory && e.Action == RdcwAction.Added)
                            {
                                rewalk.Add(e.RelativePath); // a directory moved or copied in brings its contents silently
                            }
                        }

                        break;
                    case RdcwAction.RenamedOldName:
                        pendingOld = e.RelativePath;
                        break;
                    case RdcwAction.RenamedNewName:
                        if (pendingOld is not null)
                        {
                            deltas.Add(IndexDelta.MoveByPath(pendingOld, e.RelativePath));
                        }

                        if (Stat(e, out var renamed, out var renamedIsDirectory))
                        {
                            deltas.Add(renamed); // also covers a rename whose old name was outside the watched tree
                            if (pendingOld is null && renamedIsDirectory)
                            {
                                rewalk.Add(e.RelativePath);
                            }
                        }

                        pendingOld = null;
                        break;
                }
            }

            if (overflow)
            {
                // Pending deltas may be incomplete; the subtree re-walk with reconcile restores the exact state.
                await _walker.RewalkAsync(_store, _segment, string.Empty, progress: null, ct).ConfigureAwait(false);
                OverflowRewalks++;
            }
            else
            {
                await _store.SubmitAsync(_segment.Id, deltas, ct).ConfigureAwait(false);
                foreach (var directory in rewalk.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    await _walker.RewalkAsync(_store, _segment, directory, progress: null, ct).ConfigureAwait(false);
                }

                await _store.FlushAsync(ct).ConfigureAwait(false);
            }

            EventsApplied += count;
            BatchApplied?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool Stat(RdcwEvent e, out IndexDelta delta, out bool isDirectory)
    {
        if (e.Extended && e.Attributes != 0)
        {
            isDirectory = (e.Attributes & FileAttributes.Directory) != 0;
            delta = IndexDelta.UpsertByPath(e.RelativePath, (uint)e.Attributes, isDirectory ? 0 : e.Size, e.ModifiedUtc, e.CreatedUtc);
            return true;
        }

        var full = Path.Combine(_root, e.RelativePath);
        try
        {
            FileSystemInfo info = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
            if (!info.Exists)
            {
                isDirectory = false;
                delta = default;
                return false; // already gone again; a later Removed event (or none) follows
            }

            isDirectory = info is DirectoryInfo;
            delta = IndexDelta.UpsertByPath(e.RelativePath, (uint)info.Attributes, info is FileInfo f ? f.Length : 0,
                info.LastWriteTimeUtc.ToFileTimeUtc(), info.CreationTimeUtc.ToFileTimeUtc());
            return true;
        }
        catch (IOException)
        {
            isDirectory = false;
            delta = default;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            isDirectory = false;
            delta = default;
            return false;
        }
    }
}
