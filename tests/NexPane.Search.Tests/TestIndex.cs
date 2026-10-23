using NexPane.Core.Indexing;

namespace NexPane.Search.Tests;

/// <summary>In-memory <see cref="IIndexSnapshotSource"/> built directly on <see cref="IndexSegment"/> for search tests.</summary>
internal sealed class TestIndex : IIndexSnapshotSource
{
    private readonly List<IndexSegment> _segments = [];
    private readonly List<Dictionary<string, int>> _byPath = [];
    private readonly IndexRemapLog _remap = new();
    private long _generation;
    private ulong _nextId = 100;

    public TestIndex(string root = @"C:\", VolumeKind kind = VolumeKind.NtfsFixed, ulong serial = 0x1234)
    {
        AddVolume(root, kind, serial);
    }

    public event EventHandler<IndexDeltaBatch>? DeltaApplied;

    public IIndexSnapshot Current => Generation;

    public IndexGeneration Generation { get; private set; } = IndexGeneration.Empty;

    public IndexSegment Segment => _segments[0];

    public int AddVolume(string root, VolumeKind kind = VolumeKind.NtfsFixed, ulong serial = 0)
    {
        var id = _segments.Count;
        var segment = new IndexSegment(id, new IndexVolumeInfo(root, kind, kind is VolumeKind.Ftp or VolumeKind.Sftp ? '/' : '\\', serial));
        segment.Append(5, 0, [], IndexEntryState.Directory, 0x10, 0, 0, 0);
        _segments.Add(segment);
        _byPath.Add(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [string.Empty] = 0 });
        Publish();
        return id;
    }

    /// <summary>Adds an entry by relative path (parents are created as directories). Not visible until <see cref="Publish"/>.</summary>
    public EntryRef Add(string relativePath, long size = 0, DateTime? modifiedUtc = null, uint attributes = 0, bool directory = false, int segment = 0, DateTime? createdUtc = null)
    {
        var seg = _segments[segment];
        var map = _byPath[segment];
        var separator = seg.Volume.Separator;
        var parts = relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var parent = 0;
        var current = string.Empty;
        for (var i = 0; i < parts.Length; i++)
        {
            current = current.Length == 0 ? parts[i] : current + separator + parts[i];
            var isLast = i == parts.Length - 1;
            if (map.TryGetValue(current, out var existing))
            {
                parent = existing;
                if (isLast)
                {
                    return new EntryRef(segment, existing);
                }

                continue;
            }

            var isDir = !isLast || directory;
            var attrs = isLast ? attributes | (isDir ? 0x10u : 0x20u) : 0x10u;
            var state = isDir ? IndexEntryState.Directory : IndexEntryState.None;
            if ((attrs & 0x2) != 0)
            {
                state |= IndexEntryState.Hidden;
            }

            var modified = (isLast ? modifiedUtc : null) ?? new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var created = (isLast ? createdUtc : null) ?? modified;
            var index = seg.Append(_nextId++, parent, parts[i], state, attrs, isLast ? size : 0, modified.ToFileTimeUtc(), created.ToFileTimeUtc());
            map[current] = index;
            parent = index;
        }

        return new EntryRef(segment, parent);
    }

    public void Remove(EntryRef entry) => _segments[entry.Segment].MarkDeleted(entry.Index);

    public void Rename(EntryRef entry, string newName) => _segments[entry.Segment].SetName(entry.Index, newName);

    /// <summary>Publishes a new generation and raises <see cref="DeltaApplied"/> with the given changes.</summary>
    public void Publish(IReadOnlyList<IndexChange>? changes = null, IReadOnlyList<int>? reset = null)
    {
        foreach (var s in _segments)
        {
            s.Publish();
        }

        Generation = new IndexGeneration(++_generation, [.. _segments], _remap);
        if (changes is not null || reset is not null)
        {
            DeltaApplied?.Invoke(this, new IndexDeltaBatch(_generation, changes ?? [], reset));
        }
    }

    public string PathOf(EntryRef e) => Generation.GetFullPath(e);

    public IEnumerable<EntryRef> AllLive()
    {
        var g = Generation;
        for (var s = 0; s < g.SegmentCount; s++)
        {
            for (var i = 1; i < g.Count(s); i++)
            {
                if (!g.GetSegment(s).IsDeleted(i))
                {
                    yield return new EntryRef(s, i);
                }
            }
        }
    }

    /// <summary>Synthetic tree directly in the segment: 10 % directories, realistic names, 64-way fan-out.</summary>
    public static TestIndex Synthetic(int count, int seed = 42)
    {
        var index = new TestIndex(@"D:\");
        var random = new Random(seed);
        var segment = index.Segment;
        var directories = new List<int> { 0 };
        var depths = new List<int> { 0 }; // realistic trees: depth capped at 12 levels
        string[] stems = ["report", "IMG", "invoice", "notes", "backup", "Screenshot", "project", "data", "readme", "photo", "src", "bin"];
        string[] exts = ["txt", "jpg", "pdf", "docx", "cs", "png", "zip", "mp3", "json", "log"];
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        for (var i = 0; i < count; i++)
        {
            int parent;
            var parentSlot = Math.Max(0, directories.Count - 64) + random.Next(Math.Min(64, directories.Count));
            if (random.Next(64) == 0 || depths[parentSlot] >= 12)
            {
                parentSlot = random.Next(directories.Count);
                while (depths[parentSlot] >= 12)
                {
                    parentSlot = random.Next(Math.Max(1, directories.Count / 8));
                }
            }

            parent = directories[parentSlot];

            if (i % 10 == 0)
            {
                var d = segment.Append((ulong)(1000 + i), parent, $"{stems[random.Next(stems.Length)]}_dir{i}", IndexEntryState.Directory, 0x10, 0, baseTime + i, baseTime);
                directories.Add(d);
                depths.Add(depths[parentSlot] + 1);
            }
            else
            {
                segment.Append((ulong)(1000 + i), parent, $"{stems[random.Next(stems.Length)]}_{i:D7}.{exts[random.Next(exts.Length)]}", IndexEntryState.None, 0x20,
                    random.NextInt64(0, 1L << 30), baseTime + (random.NextInt64(0, 365) * TimeSpan.TicksPerDay), baseTime);
            }
        }

        index.Publish();
        return index;
    }
}

/// <summary>Clock with a fixed instant and time zone (DST tests).</summary>
internal sealed class FixedClock(DateTimeOffset utcNow, TimeZoneInfo zone) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = utcNow;

    public override TimeZoneInfo LocalTimeZone => zone;

    public override DateTimeOffset GetUtcNow() => Now;
}
