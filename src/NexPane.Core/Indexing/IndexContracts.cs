using NexPane.Core.Vfs;

namespace NexPane.Core.Indexing;

/// <summary>An immutable generation view of the in-memory index (§10.2). Segment structure and counts are fixed;
/// existing entries may be updated in place by the writer (see <see cref="IndexSegment"/>).</summary>
public interface IIndexSnapshot
{
    long Generation { get; }

    int SegmentCount { get; }

    /// <summary>Entry count of a segment in this generation (including tombstones).</summary>
    int Count(int segment);

    /// <summary>Direct column access for scans (non-virtual hot path).</summary>
    IndexSegment GetSegment(int segment);

    ReadOnlySpan<char> GetName(EntryRef e);

    ReadOnlySpan<char> GetFoldedName(EntryRef e);

    /// <summary>Value copy of the entry. (§10.2 shows <c>ref readonly</c>; the struct-of-arrays layout returns a value — §21.)</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Member name fixed by ARCHITECTURE §10.2.")]
    IndexEntry Get(EntryRef e);

    string GetFullPath(EntryRef e);

    EntryRef? GetParent(EntryRef e);

    /// <summary>Maps an entry ref taken from generation <paramref name="fromGeneration"/> to this generation
    /// (only compaction and segment resets change refs). False when the entry no longer exists.</summary>
    bool TryRemap(EntryRef e, long fromGeneration, out EntryRef current);
}

public interface IIndexSnapshotSource
{
    IIndexSnapshot Current { get; }

    event EventHandler<IndexDeltaBatch>? DeltaApplied;
}

public interface IIndexer
{
    IReadOnlyList<VolumeStatus> Volumes { get; }

    /// <summary>Loads the snapshot, starts sources and watchers.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Flushes the snapshot.</summary>
    Task StopAsync(CancellationToken ct);

    Task RescanAsync(string volumeRoot, CancellationToken ct);

    Task AddLocationAsync(VfsPath root, IndexLocationOptions options, CancellationToken ct);

    Task RemoveLocationAsync(VfsPath root, CancellationToken ct);

    IObservable<IndexProgress> Progress { get; }
}

/// <summary>Kinds of content work (numerically equal to <c>work_queue.kind</c>).</summary>
public enum ContentWorkKind
{
    Extract = 1,
    Ocr = 2,
    Transcribe = 3,
    Describe = 4,
    Embed = 5,
}

/// <summary>Kinds of stored document text (numerically equal to <c>doc_text.kind</c>).</summary>
public enum DocTextKind
{
    Content = 1,
    Ocr = 2,
    Transcript = 3,
    AiDescription = 4,
    Metadata = 5,
}

/// <summary>One full-text hit.</summary>
public sealed record ContentHit(long DocId, string Path, double Rank, string? Snippet = null);

public interface IContentIndex
{
    Task EnqueueAsync(VfsPath path, ContentWorkKind kind, int priority, CancellationToken ct);

    IAsyncEnumerable<ContentHit> FullTextAsync(string ftsQuery, int limit, CancellationToken ct);

    Task<string?> GetTextAsync(VfsPath path, DocTextKind kind, CancellationToken ct);
}

/// <summary>Records compactions/resets so refs from older generations can be remapped (§7.1).</summary>
public sealed class IndexRemapLog
{
    private const int MaxRecords = 64;
    private readonly Lock _gate = new();
    private readonly List<Record> _records = [];
    private long _truncatedBefore;

    /// <summary>Registers that at <paramref name="generation"/> the given segment was rebuilt; <paramref name="map"/>
    /// maps old index → new index (−1 = gone). A null map means every old ref of the segment is gone.</summary>
    public void Add(long generation, int segment, int[]? map)
    {
        lock (_gate)
        {
            _records.Add(new Record(generation, segment, map));
            if (_records.Count > MaxRecords)
            {
                _truncatedBefore = _records[0].Generation;
                _records.RemoveAt(0);
            }
        }
    }

    public bool TryRemap(EntryRef e, long fromGeneration, long toGeneration, out EntryRef current)
    {
        current = e;
        lock (_gate)
        {
            if (fromGeneration < _truncatedBefore)
            {
                return false; // older than the retained history
            }

            foreach (var r in _records)
            {
                if (r.Generation <= fromGeneration || r.Generation > toGeneration || r.Segment != current.Segment)
                {
                    continue;
                }

                if (r.Map is null || current.Index >= r.Map.Length || r.Map[current.Index] < 0)
                {
                    return false;
                }

                current = new EntryRef(current.Segment, r.Map[current.Index]);
            }
        }

        return true;
    }

    private sealed record Record(long Generation, int Segment, int[]? Map);
}

/// <summary>Immutable generation: segment objects plus their published counts (§7.1).</summary>
public sealed class IndexGeneration : IIndexSnapshot
{
    private readonly IndexSegment?[] _segments;
    private readonly int[] _counts;
    private readonly IndexRemapLog _remap;

    public IndexGeneration(long generation, IndexSegment?[] segments, IndexRemapLog remap)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(remap);
        Generation = generation;
        _segments = segments;
        _remap = remap;
        _counts = new int[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            _counts[i] = segments[i]?.Count ?? 0;
        }
    }

    public static IndexGeneration Empty { get; } = new(0, [], new IndexRemapLog());

    public long Generation { get; }

    public int SegmentCount => _segments.Length;

    /// <summary>Total entries over all segments (including tombstones).</summary>
    public long TotalCount => _counts.Sum(c => (long)c);

    public int Count(int segment) => segment >= 0 && segment < _counts.Length ? _counts[segment] : 0;

    public bool HasSegment(int segment) => segment >= 0 && segment < _segments.Length && _segments[segment] is not null;

    public IndexSegment GetSegment(int segment) =>
        _segments[segment] ?? throw new ArgumentOutOfRangeException(nameof(segment), segment, "Segment slot is empty.");

    public ReadOnlySpan<char> GetName(EntryRef e) => GetSegment(e.Segment).GetName(e.Index);

    public ReadOnlySpan<char> GetFoldedName(EntryRef e) => GetSegment(e.Segment).GetFoldedName(e.Index);

    public IndexEntry Get(EntryRef e) => GetSegment(e.Segment).Get(e.Index);

    public string GetFullPath(EntryRef e) => GetSegment(e.Segment).GetFullPath(e.Index);

    public EntryRef? GetParent(EntryRef e) => e.Index == 0 ? null : new EntryRef(e.Segment, GetSegment(e.Segment).GetParent(e.Index));

    public bool IsLive(EntryRef e) => HasSegment(e.Segment) && e.Index < Count(e.Segment) && !GetSegment(e.Segment).IsDeleted(e.Index);

    public bool TryRemap(EntryRef e, long fromGeneration, out EntryRef current)
    {
        if (fromGeneration == Generation)
        {
            current = e;
            return HasSegment(e.Segment);
        }

        return _remap.TryRemap(e, fromGeneration, Generation, out current) && HasSegment(current.Segment);
    }
}
