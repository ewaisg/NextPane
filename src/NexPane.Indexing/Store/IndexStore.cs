using System.Threading.Channels;
using NexPane.Core.Indexing;

namespace NexPane.Indexing.Store;

/// <summary>Options of the in-memory index.</summary>
public sealed record IndexStoreOptions(
    int ChannelCapacity = 65_536,
    double CompactionTombstoneRatio = 0.20,
    int CompactionMinimumEntries = 4_096,
    int PathCacheCapacity = DirectoryPathCache.DefaultCapacity);

/// <summary>Handle of a registered volume segment.</summary>
public sealed record SegmentHandle(int Id, IndexVolumeInfo Volume, ulong RootFileId);

/// <summary>Rows captured for the snapshot writer (P3.08). Captured on the writer thread, written elsewhere.</summary>
public sealed record PersistBatch(int Segment, IndexVolumeInfo Volume, ulong RootFileId, bool Full, IReadOnlyList<PersistRow> Upserts, IReadOnlyList<ulong> Removed, long EntryCount);

/// <summary>One persisted entry row (<c>entries</c> table, §9.1).</summary>
public readonly record struct PersistRow(ulong FileId, ulong ParentFileId, string Name, uint Attributes, long Size, long CreatedUtc, long ModifiedUtc);

/// <summary>
/// The in-memory filename index (§7.1): a set of <see cref="IndexSegment"/>s (one per volume) mutated by exactly one
/// dedicated writer thread that consumes a bounded <c>Channel</c> of commands (capacity 65 536, <c>FullMode=Wait</c>, §5),
/// publishes a new immutable <see cref="IndexGeneration"/> after every batch and raises <see cref="DeltaApplied"/>.
/// Readers take <see cref="Current"/> without locks. Compaction runs on the writer thread when tombstones exceed 20 %.
/// </summary>
internal sealed class IndexStore : IIndexSnapshotSource, IAsyncDisposable
{
    private readonly Channel<Command> _commands;
    private readonly Thread _writer;
    private readonly IndexStoreOptions _options;
    private readonly IndexRemapLog _remap = new();
    private readonly List<SegmentState?> _segments = [];
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IndexGeneration _current;
    private long _generation;
    private long _unresolvedDeltas;
    private int _disposed;

    public IndexStore(IndexStoreOptions? options = null)
    {
        _options = options ?? new IndexStoreOptions();
        _commands = Channel.CreateBounded<Command>(new BoundedChannelOptions(_options.ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
        _current = new IndexGeneration(0, [], _remap);
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "NexPane index writer" };
        _writer.Start();
    }

    public event EventHandler<IndexDeltaBatch>? DeltaApplied;

    public IIndexSnapshot Current => Volatile.Read(ref _current);

    public IndexGeneration CurrentGeneration => Volatile.Read(ref _current);

    /// <summary>Path-addressed changes whose parent could not be resolved (indicates a needed rescan).</summary>
    public long UnresolvedDeltas => Interlocked.Read(ref _unresolvedDeltas);

    /// <summary>Total bytes held by segments and writer-side lookup tables.</summary>
    public long EstimateMemoryBytes()
    {
        long total = 0;
        lock (_segments)
        {
            foreach (var s in _segments)
            {
                if (s is not null)
                {
                    total += s.Segment.EstimateMemoryBytes() + s.Maps.AllocatedBytes;
                }
            }
        }

        return total;
    }

    /// <summary>Creates a segment for a volume. Entry 0 is the root with <paramref name="rootFileId"/>.</summary>
    public Task<SegmentHandle> RegisterVolumeAsync(IndexVolumeInfo volume, ulong rootFileId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(volume);
        return Enqueue<SegmentHandle>(tcs => new Command(CommandKind.Register, -1, null, tcs) { Volume = volume, RootFileId = rootFileId }, ct);
    }

    /// <summary>Queues changes for a segment. Waits when the channel is full (back-pressure on sources).</summary>
    public async ValueTask SubmitAsync(int segment, IReadOnlyList<IndexDelta> deltas, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deltas);
        if (deltas.Count == 0)
        {
            return;
        }

        await _commands.Writer.WriteAsync(new Command(CommandKind.Apply, segment, deltas, null), ct).ConfigureAwait(false);
    }

    /// <summary>Completes when every command queued before it has been applied and published.</summary>
    public Task FlushAsync(CancellationToken ct) => Enqueue<bool>(tcs => new Command(CommandKind.Barrier, -1, null, tcs), ct);

    /// <summary>Clears a segment (all entries except the root) for a full re-enumeration.</summary>
    public Task ResetVolumeAsync(int segment, CancellationToken ct) => Enqueue<bool>(tcs => new Command(CommandKind.Reset, segment, null, tcs), ct);

    public Task RemoveVolumeAsync(int segment, CancellationToken ct) => Enqueue<bool>(tcs => new Command(CommandKind.RemoveSegment, segment, null, tcs), ct);

    /// <summary>Forces compaction of a segment (normally automatic above the tombstone ratio).</summary>
    public Task CompactAsync(int segment, CancellationToken ct) => Enqueue<bool>(tcs => new Command(CommandKind.Compact, segment, null, tcs), ct);

    /// <summary>Full paths of entries by file id (resolved on the writer thread through the id map; null when unknown).</summary>
    public Task<IReadOnlyDictionary<ulong, string?>> ResolvePathsAsync(int segment, IReadOnlyCollection<ulong> fileIds, CancellationToken ct) =>
        Enqueue<IReadOnlyDictionary<ulong, string?>>(tcs => new Command(CommandKind.Resolve, segment, null, tcs) { FileIds = fileIds }, ct);

    public Task UpdateVolumeInfoAsync(int segment, IndexVolumeInfo volume, CancellationToken ct) =>
        Enqueue<bool>(tcs => new Command(CommandKind.SetVolume, segment, null, tcs) { Volume = volume }, ct);

    /// <summary>Captures changed rows since the previous capture (or all rows when <paramref name="full"/>).</summary>
    public Task<IReadOnlyList<PersistBatch>> CapturePersistBatchesAsync(bool full, CancellationToken ct) =>
        Enqueue<IReadOnlyList<PersistBatch>>(tcs => new Command(CommandKind.Capture, -1, null, tcs) { Full = full }, ct);

    /// <summary>Registered segment handles.</summary>
    public IReadOnlyList<SegmentHandle> Segments
    {
        get
        {
            lock (_segments)
            {
                return _segments.Where(s => s is not null).Select(s => s!.Handle).ToList();
            }
        }
    }

    public SegmentHandle? FindSegment(string rootPath)
    {
        lock (_segments)
        {
            return _segments.FirstOrDefault(s => s is not null && string.Equals(s.Handle.Volume.RootPath, rootPath, StringComparison.OrdinalIgnoreCase))?.Handle;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _commands.Writer.TryComplete();
        await _stopped.Task.ConfigureAwait(false);
    }

    private async Task<T> Enqueue<T>(Func<object, Command> create, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _commands.Writer.WriteAsync(create(tcs), ct).ConfigureAwait(false);
        return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    // ===================================================================== writer thread

    private void WriterLoop()
    {
        var reader = _commands.Reader;
        try
        {
            while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                var changes = new List<IndexChange>();
                var reset = new List<int>();
                var touched = new HashSet<int>();
                var completions = new List<Action>();
                var structural = false;
                var processed = 0;
                while (processed < 256 && reader.TryRead(out var command))
                {
                    processed++;
                    try
                    {
                        structural |= Execute(command, changes, reset, touched, completions);
                    }
                    catch (Exception ex) when (command.Completion is not null)
                    {
                        completions.Add(() => Fail(command.Completion, ex));
                    }
                }

                foreach (var segment in touched)
                {
                    var state = GetState(segment);
                    if (state is not null && ShouldCompact(state.Segment))
                    {
                        Compact(state);
                        reset.Add(segment);
                        structural = true;
                    }
                }

                if (changes.Count > 0 || structural)
                {
                    Publish(changes, reset);
                }

                foreach (var complete in completions)
                {
                    complete();
                }
            }
        }
        finally
        {
            _stopped.TrySetResult();
        }
    }

    private bool Execute(Command command, List<IndexChange> changes, List<int> reset, HashSet<int> touched, List<Action> completions)
    {
        switch (command.Kind)
        {
            case CommandKind.Register:
            {
                var id = AddSegment(command.Volume!, command.RootFileId);
                var handle = GetState(id)!.Handle;
                completions.Add(() => ((TaskCompletionSource<SegmentHandle>)command.Completion!).TrySetResult(handle));
                return true;
            }

            case CommandKind.Apply:
            {
                var state = GetState(command.Segment);
                if (state is null)
                {
                    Interlocked.Add(ref _unresolvedDeltas, command.Deltas!.Count);
                    return false;
                }

                var removedDirectory = false;
                foreach (var delta in command.Deltas!)
                {
                    removedDirectory |= Apply(state, delta, changes);
                }

                if (removedDirectory)
                {
                    SweepOrphans(state, changes);
                }

                state.Segment.Publish();
                touched.Add(state.Handle.Id);
                return false;
            }

            case CommandKind.Barrier:
                completions.Add(() => ((TaskCompletionSource<bool>)command.Completion!).TrySetResult(true));
                return false;

            case CommandKind.Reset:
            {
                var state = RequireState(command.Segment);
                ReplaceSegment(state, map: null);
                reset.Add(state.Handle.Id);
                completions.Add(() => ((TaskCompletionSource<bool>)command.Completion!).TrySetResult(true));
                return true;
            }

            case CommandKind.RemoveSegment:
            {
                lock (_segments)
                {
                    if (command.Segment >= 0 && command.Segment < _segments.Count)
                    {
                        _segments[command.Segment] = null;
                    }
                }

                _remap.Add(_generation + 1, command.Segment, null);
                reset.Add(command.Segment);
                completions.Add(() => ((TaskCompletionSource<bool>)command.Completion!).TrySetResult(true));
                return true;
            }

            case CommandKind.Compact:
            {
                var state = RequireState(command.Segment);
                Compact(state);
                reset.Add(state.Handle.Id);
                completions.Add(() => ((TaskCompletionSource<bool>)command.Completion!).TrySetResult(true));
                return true;
            }

            case CommandKind.SetVolume:
            {
                var state = RequireState(command.Segment);
                state.Segment.SetVolume(command.Volume!);
                state.Handle = state.Handle with { Volume = command.Volume! };
                state.PersistAll = true;
                completions.Add(() => ((TaskCompletionSource<bool>)command.Completion!).TrySetResult(true));
                return true;
            }

            case CommandKind.Resolve:
            {
                var state = GetState(command.Segment);
                var paths = new Dictionary<ulong, string?>();
                foreach (var id in command.FileIds ?? [])
                {
                    paths[id] = state is not null && state.Maps.TryGetById(id, out var index) ? state.Segment.GetFullPath(index) : null;
                }

                completions.Add(() => ((TaskCompletionSource<IReadOnlyDictionary<ulong, string?>>)command.Completion!).TrySetResult(paths));
                return false;
            }

            case CommandKind.Capture:
            {
                IReadOnlyList<PersistBatch> batches = Capture(command.Full);
                completions.Add(() => ((TaskCompletionSource<IReadOnlyList<PersistBatch>>)command.Completion!).TrySetResult(batches));
                return false;
            }

            default:
                throw new InvalidOperationException($"Unknown index command {command.Kind}.");
        }
    }

    private int AddSegment(IndexVolumeInfo volume, ulong rootFileId)
    {
        int id;
        lock (_segments)
        {
            id = _segments.Count;
            _segments.Add(null);
        }

        var state = NewState(id, volume, rootFileId);
        lock (_segments)
        {
            _segments[id] = state;
        }

        return id;
    }

    private SegmentState NewState(int id, IndexVolumeInfo volume, ulong rootFileId)
    {
        var segment = new IndexSegment(id, volume, _options.PathCacheCapacity);
        var root = segment.Append(rootFileId, 0, [], IndexEntryState.Directory, IndexDelta.DirectoryAttribute, 0, 0, 0);
        segment.Publish();
        var maps = new SegmentMaps(segment);
        maps.SetId(rootFileId, root);
        return new SegmentState(new SegmentHandle(id, volume, rootFileId), segment, maps) { PersistAll = true };
    }

    /// <summary>Applies one delta. Returns true when a directory was removed (orphans must be swept).</summary>
    private bool Apply(SegmentState state, in IndexDelta delta, List<IndexChange> changes)
    {
        var segment = state.Segment;
        var maps = state.Maps;
        var separator = segment.Volume.Separator;
        switch (delta.Kind)
        {
            case IndexDeltaKind.Upsert:
            {
                int parent;
                string name;
                int existing;
                if (delta.FileId != 0)
                {
                    name = delta.Name ?? throw new ArgumentException("Upsert by id needs a name.", nameof(delta));
                    if (delta.FileId == state.Handle.RootFileId)
                    {
                        return false;
                    }

                    if (!maps.TryGetById(delta.ParentFileId, out parent))
                    {
                        parent = -1;
                    }

                    maps.TryGetById(delta.FileId, out existing);
                }
                else
                {
                    var path = (delta.Path ?? throw new ArgumentException("Upsert by path needs a path.", nameof(delta))).AsSpan().Trim(separator);
                    var cut = path.LastIndexOf(separator);
                    name = (cut < 0 ? path : path[(cut + 1)..]).ToString();
                    if (name.Length == 0 || !maps.TryResolvePath(cut < 0 ? [] : path[..cut], separator, out parent))
                    {
                        Interlocked.Increment(ref _unresolvedDeltas);
                        return false;
                    }

                    existing = -1;
                    Span<char> folded = name.Length <= 256 ? stackalloc char[name.Length] : new char[name.Length];
                    NameArena.Fold(name, folded);
                    if (maps.TryGetChild(parent, folded, out var found))
                    {
                        existing = found;
                    }
                }

                var flags = FlagsFromAttributes(delta.Attributes);
                if (existing > 0)
                {
                    var oldFlags = segment.GetFlags(existing);
                    var nameChanged = !segment.GetName(existing).SequenceEqual(name);
                    var targetParent = parent < 0 ? segment.GetParent(existing) : parent;
                    var parentChanged = targetParent != segment.GetParent(existing);
                    if (nameChanged || parentChanged)
                    {
                        maps.RemoveChild(existing);
                        if (parentChanged)
                        {
                            segment.SetParent(existing, targetParent);
                        }

                        if (nameChanged)
                        {
                            segment.SetName(existing, name);
                        }

                        maps.AddChild(existing);
                        changes.Add(new IndexChange(IndexChangeKind.Renamed, new EntryRef(segment.Id, existing)));
                    }

                    var newFlags = (oldFlags & IndexEntryState.ContentIndexed) | flags;
                    if (newFlags != oldFlags)
                    {
                        segment.SetFlags(existing, newFlags);
                    }

                    if (segment.GetAttributes(existing) != delta.Attributes || segment.GetSize(existing) != delta.Size ||
                        segment.GetModified(existing) != delta.ModifiedUtc || segment.GetCreated(existing) != delta.CreatedUtc)
                    {
                        segment.SetStat(existing, delta.Attributes, delta.Size, delta.ModifiedUtc, delta.CreatedUtc);
                        if (!nameChanged && !parentChanged)
                        {
                            changes.Add(new IndexChange(IndexChangeKind.Modified, new EntryRef(segment.Id, existing)));
                        }
                    }

                    MarkDirty(state, existing);
                    return false;
                }

                var fileId = delta.FileId != 0 ? delta.FileId : state.NextSyntheticId();
                state.ObserveFileId(fileId);
                var index = segment.Append(fileId, parent < 0 ? 0 : parent, name, flags, delta.Attributes, delta.Size, delta.ModifiedUtc, delta.CreatedUtc);
                maps.SetId(fileId, index);
                if (parent < 0)
                {
                    state.AddPending(delta.ParentFileId, index);
                }
                else
                {
                    maps.AddChild(index);
                }

                AdoptPendingChildren(state, fileId, index);
                changes.Add(new IndexChange(IndexChangeKind.Added, new EntryRef(segment.Id, index)));
                MarkDirty(state, index);
                return false;
            }

            case IndexDeltaKind.Remove:
            {
                if (!TryFind(state, delta.FileId, delta.Path, out var index) || index == 0)
                {
                    return false;
                }

                var isDirectory = segment.IsDirectory(index);
                RemoveEntry(state, index, changes);
                return isDirectory;
            }

            case IndexDeltaKind.Move:
            {
                if (!TryFind(state, delta.FileId, delta.Path, out var index) || index == 0)
                {
                    Interlocked.Increment(ref _unresolvedDeltas);
                    return false;
                }

                int newParent;
                string newName;
                if (delta.FileId != 0)
                {
                    newName = delta.Name ?? segment.GetName(index).ToString();
                    if (!maps.TryGetById(delta.ParentFileId, out newParent))
                    {
                        // Moved out of the indexed tree.
                        var wasDirectory = segment.IsDirectory(index);
                        RemoveEntry(state, index, changes);
                        return wasDirectory;
                    }
                }
                else
                {
                    var target = (delta.NewPath ?? throw new ArgumentException("Move by path needs a new path.", nameof(delta))).AsSpan().Trim(separator);
                    var cut = target.LastIndexOf(separator);
                    newName = (cut < 0 ? target : target[(cut + 1)..]).ToString();
                    if (!maps.TryResolvePath(cut < 0 ? [] : target[..cut], separator, out newParent))
                    {
                        var wasDirectory = segment.IsDirectory(index);
                        RemoveEntry(state, index, changes);
                        return wasDirectory;
                    }
                }

                // Replace an existing entry at the destination (rename over an existing name).
                Span<char> folded = newName.Length <= 256 ? stackalloc char[newName.Length] : new char[newName.Length];
                NameArena.Fold(newName, folded);
                var removedDirectory = false;
                if (maps.TryGetChild(newParent, folded, out var clash) && clash != index)
                {
                    removedDirectory = segment.IsDirectory(clash);
                    RemoveEntry(state, clash, changes);
                }

                maps.RemoveChild(index);
                segment.SetParent(index, newParent);
                segment.SetName(index, newName);
                maps.AddChild(index);
                changes.Add(new IndexChange(IndexChangeKind.Renamed, new EntryRef(segment.Id, index)));
                MarkDirty(state, index);
                return removedDirectory;
            }

            case IndexDeltaKind.Reconcile:
            {
                if (!TryFind(state, 0, delta.Path ?? string.Empty, out var root))
                {
                    return false;
                }

                var found = delta.Found ?? new HashSet<string>();
                var count = segment.AllocatedCount;
                var any = false;
                for (var i = 1; i < count; i++)
                {
                    if (segment.IsDeleted(i) || !IsDescendant(segment, i, root))
                    {
                        continue;
                    }

                    var relative = RelativePath(segment, i);
                    if (!found.Contains(relative))
                    {
                        any |= segment.IsDirectory(i);
                        RemoveEntry(state, i, changes);
                    }
                }

                return any;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(delta), delta.Kind, "Unknown delta kind.");
        }
    }

    private static bool TryFind(SegmentState state, ulong fileId, string? path, out int index)
    {
        if (fileId != 0)
        {
            return state.Maps.TryGetById(fileId, out index);
        }

        if (path is null)
        {
            index = -1;
            return false;
        }

        return state.Maps.TryResolvePath(path, state.Segment.Volume.Separator, out index);
    }

    private static void RemoveEntry(SegmentState state, int index, List<IndexChange> changes)
    {
        var segment = state.Segment;
        if (segment.IsDeleted(index))
        {
            return;
        }

        state.Maps.RemoveChild(index);
        state.Maps.RemoveId(segment.GetFileId(index));
        segment.MarkDeleted(index);
        state.Removed.Add(segment.GetFileId(index));
        state.Dirty.Remove(index);
        changes.Add(new IndexChange(IndexChangeKind.Removed, new EntryRef(segment.Id, index)));
    }

    /// <summary>Removes live entries whose ancestor chain contains a deleted directory (single pass with memo).</summary>
    private static void SweepOrphans(SegmentState state, List<IndexChange> changes)
    {
        var segment = state.Segment;
        var count = segment.AllocatedCount;
        // 0 unknown, 1 live chain, 2 dead chain
        var memo = new byte[count];
        memo[0] = 1;
        var stack = new Stack<int>();
        for (var i = 1; i < count; i++)
        {
            if (memo[i] != 0)
            {
                continue;
            }

            var current = i;
            while (memo[current] == 0)
            {
                if (segment.IsDeleted(current))
                {
                    memo[current] = 2;
                    break;
                }

                stack.Push(current);
                var parent = segment.GetParent(current);
                if (parent == current || stack.Count > count)
                {
                    memo[current] = 1;
                    stack.Pop();
                    break;
                }

                current = parent;
            }

            var verdict = memo[current];
            while (stack.Count > 0)
            {
                var item = stack.Pop();
                memo[item] = verdict;
                if (verdict == 2 && !segment.IsDeleted(item))
                {
                    RemoveEntry(state, item, changes);
                }
            }
        }
    }

    private static void AdoptPendingChildren(SegmentState state, ulong fileId, int parentIndex)
    {
        if (state.Pending is null || !state.Pending.Remove(fileId, out var children))
        {
            return;
        }

        foreach (var child in children)
        {
            if (state.Segment.IsDeleted(child))
            {
                continue;
            }

            state.Segment.SetParent(child, parentIndex);
            state.Maps.AddChild(child);
            MarkDirty(state, child);
        }
    }

    private static bool IsDescendant(IndexSegment segment, int index, int ancestor)
    {
        if (ancestor == 0)
        {
            return true;
        }

        var current = segment.GetParent(index);
        for (var guard = 0; guard < 4096 && current != 0; guard++)
        {
            if (current == ancestor)
            {
                return true;
            }

            current = segment.GetParent(current);
        }

        return false;
    }

    private static string RelativePath(IndexSegment segment, int index)
    {
        var parts = new List<string>();
        var current = index;
        for (var guard = 0; guard < 4096 && current != 0; guard++)
        {
            parts.Add(segment.GetName(current).ToString());
            current = segment.GetParent(current);
        }

        parts.Reverse();
        return string.Join(segment.Volume.Separator, parts);
    }

    private bool ShouldCompact(IndexSegment segment)
    {
        var count = segment.Count;
        return count >= _options.CompactionMinimumEntries && segment.TombstoneCount > count * _options.CompactionTombstoneRatio;
    }

    /// <summary>Builds a new segment with only live entries and swaps it in; old refs are remapped through the log.</summary>
    private void Compact(SegmentState state)
    {
        var old = state.Segment;
        var count = old.AllocatedCount;
        var map = new int[count];
        var next = 0;
        for (var i = 0; i < count; i++)
        {
            map[i] = i == 0 || !old.IsDeleted(i) ? next++ : -1;
        }

        ReplaceSegment(state, map);
    }

    /// <summary>Replaces the segment object: with a map, copies live entries (compaction); without, keeps only the root (reset).</summary>
    private void ReplaceSegment(SegmentState state, int[]? map)
    {
        var old = state.Segment;
        var fresh = new IndexSegment(old.Id, old.Volume, _options.PathCacheCapacity);
        var maps = new SegmentMaps(fresh);
        fresh.Append(old.GetFileId(0), 0, [], IndexEntryState.Directory, IndexDelta.DirectoryAttribute, 0, 0, 0);
        maps.SetId(old.GetFileId(0), 0);
        if (map is not null)
        {
            for (var i = 1; i < map.Length; i++)
            {
                if (map[i] < 0)
                {
                    continue;
                }

                var parent = old.GetParent(i);
                var newParent = parent >= 0 && parent < map.Length && map[parent] >= 0 ? map[parent] : 0;
                var index = fresh.Append(old.GetFileId(i), newParent, old.GetName(i), old.GetFlags(i), old.GetAttributes(i),
                    old.GetSize(i), old.GetModified(i), old.GetCreated(i));
                maps.SetId(old.GetFileId(i), index);
            }

            for (var i = 1; i < fresh.AllocatedCount; i++)
            {
                maps.AddChild(i);
            }

            // Pending (orphan) children keep the root as their interim parent; remap their indexes.
            if (state.Pending is not null)
            {
                foreach (var list in state.Pending.Values)
                {
                    for (var k = list.Count - 1; k >= 0; k--)
                    {
                        var mapped = list[k] < map.Length ? map[list[k]] : -1;
                        if (mapped < 0)
                        {
                            list.RemoveAt(k);
                        }
                        else
                        {
                            list[k] = mapped;
                        }
                    }
                }
            }

            state.Dirty = state.Dirty.Remap(map);
        }
        else
        {
            foreach (var i in Enumerable.Range(1, Math.Max(0, old.AllocatedCount - 1)))
            {
                if (!old.IsDeleted(i))
                {
                    state.Removed.Add(old.GetFileId(i));
                }
            }

            state.Pending?.Clear();
            state.Dirty.Clear();
            state.ResetSyntheticIds();
        }

        fresh.Publish();
        state.Segment = fresh;
        state.Maps = maps;
        _remap.Add(_generation + 1, old.Id, map);
    }

    private void Publish(List<IndexChange> changes, List<int> reset)
    {
        IndexSegment?[] segments;
        lock (_segments)
        {
            segments = _segments.Select(s => s?.Segment).ToArray();
        }

        var generation = new IndexGeneration(++_generation, segments, _remap);
        Volatile.Write(ref _current, generation);
        var handler = DeltaApplied;
        handler?.Invoke(this, new IndexDeltaBatch(generation.Generation, changes, reset.Distinct().ToList()));
    }

    private List<PersistBatch> Capture(bool full)
    {
        var result = new List<PersistBatch>();
        List<SegmentState> states;
        lock (_segments)
        {
            states = _segments.Where(s => s is not null).Select(s => s!).ToList();
        }

        foreach (var state in states)
        {
            var segment = state.Segment;
            var all = full || state.PersistAll;
            var rows = new List<PersistRow>();
            IEnumerable<int> indexes = all ? Enumerable.Range(1, Math.Max(0, segment.AllocatedCount - 1)) : state.Dirty.Enumerate();
            foreach (var i in indexes)
            {
                if (i <= 0 || i >= segment.AllocatedCount || segment.IsDeleted(i))
                {
                    continue;
                }

                rows.Add(new PersistRow(segment.GetFileId(i), segment.GetFileId(segment.GetParent(i)), segment.GetName(i).ToString(),
                    segment.GetAttributes(i), segment.GetSize(i), segment.GetCreated(i), segment.GetModified(i)));
            }

            var removed = all ? [] : state.Removed.ToList();
            if (all || rows.Count > 0 || removed.Count > 0)
            {
                result.Add(new PersistBatch(segment.Id, segment.Volume, state.Handle.RootFileId, all, rows, removed, segment.LiveCount - 1));
            }

            state.Dirty.Clear();
            state.Removed.Clear();
            state.PersistAll = false;
        }

        return result;
    }

    private static void MarkDirty(SegmentState state, int index)
    {
        if (!state.PersistAll)
        {
            state.Dirty.Add(index);
        }
    }

    private static IndexEntryState FlagsFromAttributes(uint attributes)
    {
        var flags = IndexEntryState.None;
        if ((attributes & 0x10) != 0)
        {
            flags |= IndexEntryState.Directory;
        }

        if ((attributes & 0x2) != 0)
        {
            flags |= IndexEntryState.Hidden;
        }

        if ((attributes & 0x4) != 0)
        {
            flags |= IndexEntryState.System;
        }

        if ((attributes & 0x400) != 0)
        {
            flags |= IndexEntryState.ReparsePoint;
        }

        return flags;
    }

    private SegmentState? GetState(int segment)
    {
        lock (_segments)
        {
            return segment >= 0 && segment < _segments.Count ? _segments[segment] : null;
        }
    }

    private SegmentState RequireState(int segment) =>
        GetState(segment) ?? throw new ArgumentOutOfRangeException(nameof(segment), segment, "Unknown segment.");

    private static void Fail(object completion, Exception ex)
    {
        switch (completion)
        {
            case TaskCompletionSource<bool> b:
                b.TrySetException(ex);
                break;
            case TaskCompletionSource<SegmentHandle> h:
                h.TrySetException(ex);
                break;
            case TaskCompletionSource<IReadOnlyList<PersistBatch>> p:
                p.TrySetException(ex);
                break;
            case TaskCompletionSource<IReadOnlyDictionary<ulong, string?>> r:
                r.TrySetException(ex);
                break;
            default:
                throw new InvalidOperationException("Unknown completion type.", ex);
        }
    }

    private enum CommandKind
    {
        Register,
        Apply,
        Barrier,
        Reset,
        RemoveSegment,
        Compact,
        SetVolume,
        Capture,
        Resolve,
    }

    private sealed record Command(CommandKind Kind, int Segment, IReadOnlyList<IndexDelta>? Deltas, object? Completion)
    {
        public IndexVolumeInfo? Volume { get; init; }

        public ulong RootFileId { get; init; }

        public bool Full { get; init; }

        public IReadOnlyCollection<ulong>? FileIds { get; init; }
    }

    private sealed class SegmentState(SegmentHandle handle, IndexSegment segment, SegmentMaps maps)
    {
        private const ulong SyntheticBase = 1UL << 62;
        private ulong _nextSynthetic = SyntheticBase;

        public SegmentHandle Handle { get; set; } = handle;

        public IndexSegment Segment { get; set; } = segment;

        public SegmentMaps Maps { get; set; } = maps;

        public Dictionary<ulong, List<int>>? Pending { get; private set; }

        public DirtySet Dirty { get; set; } = new();

        public HashSet<ulong> Removed { get; } = [];

        public bool PersistAll { get; set; }

        public ulong NextSyntheticId() => _nextSynthetic++;

        public void ObserveFileId(ulong fileId)
        {
            if (fileId >= _nextSynthetic && fileId >= SyntheticBase)
            {
                _nextSynthetic = fileId + 1;
            }
        }

        public void ResetSyntheticIds() => _nextSynthetic = SyntheticBase;

        public void AddPending(ulong parentFileId, int child)
        {
            Pending ??= [];
            if (!Pending.TryGetValue(parentFileId, out var list))
            {
                list = [];
                Pending[parentFileId] = list;
            }

            list.Add(child);
        }
    }
}
