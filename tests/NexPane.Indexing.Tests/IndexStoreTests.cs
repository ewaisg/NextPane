using NexPane.Core.Indexing;
using NexPane.Indexing.Store;

namespace NexPane.Indexing.Tests;

public sealed class IndexStoreTests : IAsyncLifetime
{
    private const ulong Root = 5;
    private IndexStore _store = null!;
    private SegmentHandle _volume = null!;
    private readonly List<IndexDeltaBatch> _batches = [];

    public async ValueTask InitializeAsync()
    {
        _store = new IndexStore();
        _store.DeltaApplied += (_, b) =>
        {
            lock (_batches)
            {
                _batches.Add(b);
            }
        };
        _volume = await _store.RegisterVolumeAsync(new IndexVolumeInfo(@"C:\", VolumeKind.NtfsFixed), Root, Ct);
    }

    public async ValueTask DisposeAsync() => await _store.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Added_entries_are_visible_with_full_paths_and_stat()
    {
        await Apply(
            IndexDelta.UpsertById(100, Root, "Users", 0x10, 0, 10, 11),
            IndexDelta.UpsertById(101, 100, "Report Q3.PDF", 0x20, 4096, 20, 21));

        var snapshot = _store.CurrentGeneration;
        var file = Find(snapshot, "Report Q3.PDF");
        snapshot.GetFullPath(file).Should().Be(@"C:\Users\Report Q3.PDF");
        snapshot.GetFoldedName(file).ToString().Should().Be("REPORT Q3.PDF");
        var entry = snapshot.Get(file);
        entry.Size.Should().Be(4096);
        entry.ModifiedUtc.Should().Be(20);
        entry.CreatedUtc.Should().Be(21);
        entry.FileId.Should().Be(101UL);
        entry.IsDirectory.Should().BeFalse();
        snapshot.GetParent(file)!.Value.Index.Should().Be(Find(snapshot, "Users").Index);
        snapshot.GetSegment(file.Segment).GetFoldedExtension(file.Index).ToString().Should().Be("PDF");
        LastChanges().Should().Contain(c => c.Kind == IndexChangeKind.Added && c.Entry == file);
    }

    [Fact]
    public async Task Remove_tombstones_entry_and_directory_removal_removes_subtree()
    {
        await Apply(
            IndexDelta.UpsertById(100, Root, "a", 0x10, 0, 1, 1),
            IndexDelta.UpsertById(101, 100, "b", 0x10, 0, 1, 1),
            IndexDelta.UpsertById(102, 101, "c.txt", 0x20, 1, 1, 1),
            IndexDelta.UpsertById(103, Root, "keep.txt", 0x20, 1, 1, 1));
        var before = _store.CurrentGeneration;
        var c = Find(before, "c.txt");

        await Apply(IndexDelta.RemoveById(100));

        var snapshot = _store.CurrentGeneration;
        snapshot.IsLive(c).Should().BeFalse();
        LiveNames(snapshot).Should().BeEquivalentTo("keep.txt");
        snapshot.GetSegment(0).TombstoneCount.Should().Be(3);
        LastChanges()!.Where(x => x.Kind == IndexChangeKind.Removed).Should().HaveCount(3);
    }

    [Fact]
    public async Task Rename_and_move_update_in_place_and_invalidate_paths()
    {
        await Apply(
            IndexDelta.UpsertById(100, Root, "src", 0x10, 0, 1, 1),
            IndexDelta.UpsertById(101, 100, "app", 0x10, 0, 1, 1),
            IndexDelta.UpsertById(102, 101, "main.cs", 0x20, 1, 1, 1),
            IndexDelta.UpsertById(103, Root, "dst", 0x10, 0, 1, 1));
        var main = Find(_store.CurrentGeneration, "main.cs");
        _store.CurrentGeneration.GetFullPath(main).Should().Be(@"C:\src\app\main.cs"); // warms the path cache

        await Apply(IndexDelta.MoveById(101, 103, "application"));

        var snapshot = _store.CurrentGeneration;
        snapshot.GetFullPath(main).Should().Be(@"C:\dst\application\main.cs");
        LastChanges().Should().ContainSingle(x => x.Kind == IndexChangeKind.Renamed);

        await Apply(IndexDelta.MoveById(102, 103, "Program.cs"));
        _store.CurrentGeneration.GetFullPath(main).Should().Be(@"C:\dst\Program.cs");
    }

    [Fact]
    public async Task Upsert_of_existing_entry_reports_modified_only_when_stat_changes()
    {
        await Apply(IndexDelta.UpsertById(100, Root, "log.txt", 0x20, 10, 1, 1));
        await Apply(IndexDelta.UpsertById(100, Root, "log.txt", 0x20, 10, 1, 1));
        LastChanges().Should().BeNull("an identical upsert changes nothing and publishes no batch");

        await Apply(IndexDelta.UpsertById(100, Root, "log.txt", 0x20, 99, 2, 1));

        LastChanges().Should().ContainSingle(c => c.Kind == IndexChangeKind.Modified);
        _store.CurrentGeneration.Get(Find(_store.CurrentGeneration, "log.txt")).Size.Should().Be(99);
    }

    [Fact]
    public async Task Children_enumerated_before_their_parent_are_adopted()
    {
        await Apply(
            IndexDelta.UpsertById(300, 200, "late-child.txt", 0x20, 1, 1, 1),
            IndexDelta.UpsertById(200, Root, "parent", 0x10, 0, 1, 1));

        var snapshot = _store.CurrentGeneration;
        snapshot.GetFullPath(Find(snapshot, "late-child.txt")).Should().Be(@"C:\parent\late-child.txt");
    }

    [Fact]
    public async Task Path_addressed_changes_resolve_through_the_child_map()
    {
        await Apply(
            IndexDelta.UpsertByPath(@"Docs", 0x10, 0, 1, 1),
            IndexDelta.UpsertByPath(@"Docs\Tax", 0x10, 0, 1, 1),
            IndexDelta.UpsertByPath(@"docs\TAX\return.pdf", 0x20, 5, 1, 1));
        var file = Find(_store.CurrentGeneration, "return.pdf");
        _store.CurrentGeneration.GetFullPath(file).Should().Be(@"C:\Docs\Tax\return.pdf");

        await Apply(IndexDelta.MoveByPath(@"Docs\Tax", @"Archive2025"));
        _store.CurrentGeneration.GetFullPath(file).Should().Be(@"C:\Archive2025\return.pdf");

        await Apply(IndexDelta.UpsertByPath(@"missing\dir\x.txt", 0x20, 1, 1, 1));
        _store.UnresolvedDeltas.Should().Be(1);

        await Apply(IndexDelta.RemoveByPath(@"archive2025\RETURN.pdf"));
        _store.CurrentGeneration.IsLive(file).Should().BeFalse();
    }

    [Fact]
    public async Task Reconcile_removes_entries_missing_from_a_rewalk()
    {
        await Apply(
            IndexDelta.UpsertByPath("w", 0x10, 0, 1, 1),
            IndexDelta.UpsertByPath(@"w\keep.txt", 0x20, 0, 1, 1),
            IndexDelta.UpsertByPath(@"w\gone.txt", 0x20, 0, 1, 1),
            IndexDelta.UpsertByPath(@"w\sub", 0x10, 0, 1, 1),
            IndexDelta.UpsertByPath(@"w\sub\gone2.txt", 0x20, 0, 1, 1),
            IndexDelta.UpsertByPath(@"outside.txt", 0x20, 0, 1, 1));

        await Apply(IndexDelta.ReconcileSubtree("w", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"w\keep.txt", @"w\sub" }));

        LiveNames(_store.CurrentGeneration).Should().BeEquivalentTo("w", "keep.txt", "sub", "outside.txt");
    }

    [Fact]
    public async Task Rename_over_an_existing_name_replaces_it()
    {
        await Apply(
            IndexDelta.UpsertById(100, Root, "a.txt", 0x20, 1, 1, 1),
            IndexDelta.UpsertById(101, Root, "b.txt", 0x20, 2, 1, 1));

        await Apply(IndexDelta.MoveById(100, Root, "b.txt"));

        var snapshot = _store.CurrentGeneration;
        LiveNames(snapshot).Should().BeEquivalentTo("b.txt");
        snapshot.Get(Find(snapshot, "b.txt")).FileId.Should().Be(100UL);
    }

    [Fact]
    public async Task Reset_clears_a_volume_and_old_refs_no_longer_remap()
    {
        await Apply(IndexDelta.UpsertById(100, Root, "x", 0x20, 1, 1, 1));
        var old = _store.CurrentGeneration;
        var x = Find(old, "x");

        await _store.ResetVolumeAsync(_volume.Id, Ct);

        var snapshot = _store.CurrentGeneration;
        snapshot.Count(_volume.Id).Should().Be(1);
        snapshot.TryRemap(x, old.Generation, out _).Should().BeFalse();
        _batches.Last().ResetSegments.Should().Contain(_volume.Id);
    }

    [Fact]
    public async Task Compaction_after_30_percent_deletes_keeps_query_results_correct()
    {
        var random = new Random(17);
        var deltas = new List<IndexDelta>();
        for (ulong d = 0; d < 100; d++)
        {
            deltas.Add(IndexDelta.UpsertById(1000 + d, Root, "dir" + d, 0x10, 0, 1, 1));
        }

        for (ulong f = 0; f < 20_000; f++)
        {
            deltas.Add(IndexDelta.UpsertById(100_000 + f, 1000 + (ulong)random.Next(100), $"file{f}_{(f % 7 == 0 ? "match" : "other")}.dat", 0x20, (long)f, 1, 1));
        }

        await Apply([.. deltas]);
        var before = _store.CurrentGeneration;
        var liveQuery = Scan(before, "MATCH").ToList();
        var removedIds = Enumerable.Range(0, 20_000).Where(_ => random.NextDouble() < 0.30).Select(f => 100_000UL + (ulong)f).ToHashSet();

        await Apply([.. removedIds.Select(IndexDelta.RemoveById)]);

        var after = _store.CurrentGeneration;
        after.GetSegment(_volume.Id).TombstoneCount.Should().Be(0, "compaction ran automatically above the 20 % tombstone ratio");
        _batches.Last().ResetSegments.Should().Contain(_volume.Id);

        // Remap the earlier live-query result into the compacted generation and compare against a brute-force recompute.
        var remapped = liveQuery.Select(r => after.TryRemap(r, before.Generation, out var now) ? now : (EntryRef?)null)
            .Where(r => r is not null).Select(r => r!.Value).ToList();
        var brute = Scan(after, "MATCH").ToList();
        remapped.Should().BeEquivalentTo(brute);
        brute.Select(r => after.Get(r).FileId).Should().NotContain(id => removedIds.Contains(id));
        brute.Should().HaveCount(liveQuery.Count - liveQuery.Count(r => removedIds.Contains(before.Get(r).FileId)));
        brute.Should().AllSatisfy(r => after.GetFullPath(r).Should().StartWith(@"C:\dir"));

        // The compacted segment keeps accepting changes by file id.
        var survivor = brute[0];
        await Apply(IndexDelta.MoveById(after.Get(survivor).FileId, Root, "moved.dat"));
        _store.CurrentGeneration.GetFullPath(survivor).Should().Be(@"C:\moved.dat");
    }

    [Fact]
    public async Task Deep_200_level_tree_reconstructs_paths_and_uses_the_cache()
    {
        var deltas = new List<IndexDelta>();
        var parent = Root;
        for (ulong level = 0; level < 200; level++)
        {
            deltas.Add(IndexDelta.UpsertById(10 + level, parent, "L" + level, 0x10, 0, 1, 1));
            parent = 10 + level;
        }

        deltas.Add(IndexDelta.UpsertById(5000, parent, "leaf.txt", 0x20, 1, 1, 1));
        await Apply([.. deltas]);

        var snapshot = _store.CurrentGeneration;
        var leaf = Find(snapshot, "leaf.txt");
        var expected = @"C:\" + string.Join('\\', Enumerable.Range(0, 200).Select(i => "L" + i)) + @"\leaf.txt";
        snapshot.GetFullPath(leaf).Should().Be(expected);
        snapshot.GetSegment(leaf.Segment).GetDepth(leaf.Index).Should().Be(201);
        var (_, missesBefore) = snapshot.GetSegment(leaf.Segment).PathCacheStats;
        snapshot.GetFullPath(leaf).Should().Be(expected);
        snapshot.GetSegment(leaf.Segment).PathCacheStats.Misses.Should().Be(missesBefore, "the second lookup is a cache hit");

        await Apply(IndexDelta.MoveById(10 + 100, 10 + 98, "R100"));
        var moved = _store.CurrentGeneration.GetFullPath(leaf);
        moved.Should().Contain(@"\L98\R100\L101\").And.NotContain(@"\L99\");
    }

    [Fact]
    public async Task Multiple_volumes_are_separate_segments()
    {
        var d = await _store.RegisterVolumeAsync(new IndexVolumeInfo(@"\\nas\media", VolumeKind.Smb), 1, Ct);
        await _store.SubmitAsync(d.Id, [IndexDelta.UpsertByPath(@"films\a.mkv", 0x20, 1, 1, 1)], Ct);
        await _store.SubmitAsync(d.Id, [IndexDelta.UpsertByPath(@"films", 0x10, 0, 1, 1), IndexDelta.UpsertByPath(@"films\a.mkv", 0x20, 1, 1, 1)], Ct);
        await _store.FlushAsync(Ct);

        var snapshot = _store.CurrentGeneration;
        snapshot.SegmentCount.Should().Be(2);
        var a = Find(snapshot, "a.mkv");
        a.Segment.Should().Be(d.Id);
        snapshot.GetFullPath(a).Should().Be(@"\\nas\media\films\a.mkv");
        _store.FindSegment(@"\\NAS\media")!.Id.Should().Be(d.Id);
    }

    [Fact]
    public async Task Persist_capture_returns_changed_rows_then_only_new_changes()
    {
        await Apply(IndexDelta.UpsertById(100, Root, "a", 0x20, 1, 2, 3), IndexDelta.UpsertById(101, Root, "b", 0x20, 1, 2, 3));
        var first = await _store.CapturePersistBatchesAsync(full: false, Ct);
        first.Should().ContainSingle().Which.Full.Should().BeTrue("a new volume is persisted completely once");
        first[0].Upserts.Select(r => r.Name).Should().BeEquivalentTo("a", "b");

        await Apply(IndexDelta.UpsertById(100, Root, "a", 0x20, 50, 2, 3), IndexDelta.RemoveById(101));
        var second = await _store.CapturePersistBatchesAsync(full: false, Ct);

        second.Should().ContainSingle();
        second[0].Full.Should().BeFalse();
        second[0].Upserts.Should().ContainSingle(r => r.Name == "a" && r.Size == 50 && r.ParentFileId == Root);
        second[0].Removed.Should().Equal(101UL);
        (await _store.CapturePersistBatchesAsync(full: false, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Eight_readers_and_one_writer_over_one_million_operations_observe_no_torn_reads()
    {
        const int files = 2_000;
        var initial = Enumerable.Range(0, files).Select(i => IndexDelta.UpsertById(10_000 + (ulong)i, Root, Name(i, 0), 0x20, Stat(i, 0), Stat(i, 0), 1)).ToArray();
        await Apply(initial);

        using var stop = new CancellationTokenSource();
        var failures = 0L;
        var reads = 0L;
        var readers = Enumerable.Range(0, 8).Select(r => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var snapshot = _store.CurrentGeneration;
                var segment = snapshot.GetSegment(_volume.Id);
                var count = snapshot.Count(_volume.Id);
                for (var i = 1; i < count; i++)
                {
                    var name = segment.GetName(i);
                    var folded = segment.GetFoldedName(i);
                    // Every name must be one of the well-formed names the writer wrote: "f<id>_v<version>.bin".
                    if (!IsWellFormed(name) || !IsWellFormed(folded.ToString().ToLowerInvariant()) || !folded.ToString().Equals(folded.ToString().ToUpperInvariant(), StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref failures);
                    }

                    // Size and mtime are written with independent atomic stores; each must be a value the writer produced.
                    var size = segment.GetSize(i);
                    if (size % 1_000_000 != i - 1 && size % 1_000_000 != i)
                    {
                        Interlocked.Increment(ref failures);
                    }

                    _ = snapshot.GetFullPath(new EntryRef(_volume.Id, i));
                }

                Interlocked.Increment(ref reads);
            }
        })).ToArray();

        var ops = 0;
        var version = 1;
        while (ops < 1_000_000)
        {
            var batch = new IndexDelta[1_000];
            for (var k = 0; k < batch.Length; k++, ops++)
            {
                var i = ops % files;
                batch[k] = (ops & 1) == 0
                    ? IndexDelta.MoveById(10_000 + (ulong)i, Root, Name(i, version))
                    : IndexDelta.UpsertById(10_000 + (ulong)i, Root, Name(i, version), 0x20, Stat(i, version), Stat(i, version), 1);
            }

            version++;
            await _store.SubmitAsync(_volume.Id, batch, Ct);
        }

        await _store.FlushAsync(Ct);
        await stop.CancelAsync();
        await Task.WhenAll(readers);

        Interlocked.Read(ref failures).Should().Be(0);
        Interlocked.Read(ref reads).Should().BeGreaterThan(8);
        _store.CurrentGeneration.Count(_volume.Id).Should().Be(files + 1);

        static string Name(int i, int v) => $"f{i}_v{v}.bin";
        static long Stat(int i, int v) => ((long)v * 1_000_000) + i + 1;
        static bool IsWellFormed(ReadOnlySpan<char> name) =>
            name.Length > 6 && name[0] == 'f' && name.EndsWith(".bin") && name.Contains("_v", StringComparison.Ordinal);
    }

    /// <summary>1M entries: 10 % directories, average name ≈ 18 chars.</summary>
    internal static IEnumerable<IndexDelta> SyntheticTree(int count, ulong root = Root)
    {
        var random = new Random(42);
        var directories = new List<ulong> { root };
        string[] stems = ["report", "IMG", "invoice", "notes", "backup", "Screenshot", "project", "data", "readme", "photo"];
        string[] exts = ["txt", "jpg", "pdf", "docx", "cs", "png", "zip", "mp3", "json", "log"];
        for (var i = 0; i < count; i++)
        {
            var id = 1_000UL + (ulong)i;
            var parent = directories[random.Next(directories.Count)];
            if (i % 10 == 0)
            {
                directories.Add(id);
                yield return IndexDelta.UpsertById(id, parent, $"{stems[random.Next(stems.Length)]}_folder_{i}", 0x10, 0, 132_000_000_000_000_000 + i, 132_000_000_000_000_000);
            }
            else
            {
                yield return IndexDelta.UpsertById(id, parent, $"{stems[random.Next(stems.Length)]}_{i:D6}.{exts[random.Next(exts.Length)]}", 0x20,
                    random.NextInt64(0, 1L << 32), 132_000_000_000_000_000 + i, 132_000_000_000_000_000);
            }
        }
    }

    /// <summary>Expected full paths of a delta stream (Upsert by id only), for verifying reconstructed paths.</summary>
    internal sealed class ExpectedTree
    {
        private readonly Dictionary<ulong, (ulong Parent, string Name)> _entries = [];
        private readonly string _root;

        public ExpectedTree(IEnumerable<IndexDelta> deltas, string root)
        {
            _root = root;
            foreach (var d in deltas)
            {
                _entries[d.FileId] = (d.ParentFileId, d.Name!);
            }
        }

        public string PathOf(ulong id)
        {
            var parts = new List<string>();
            while (_entries.TryGetValue(id, out var e))
            {
                parts.Add(e.Name);
                id = e.Parent;
            }

            parts.Reverse();
            return _root + string.Join('\\', parts);
        }

        public IEnumerable<ulong> Sample(int count, int seed)
        {
            var keys = _entries.Keys.ToArray();
            var random = new Random(seed);
            return Enumerable.Range(0, count).Select(_ => keys[random.Next(keys.Length)]);
        }
    }

    private async Task Apply(params IndexDelta[] deltas)
    {
        lock (_batches)
        {
            _batches.Clear();
        }

        await _store.SubmitAsync(_volume.Id, deltas, Ct);
        await _store.FlushAsync(Ct);
    }

    private List<IndexChange>? LastChanges()
    {
        lock (_batches)
        {
            return _batches.Count == 0 ? null : _batches.SelectMany(b => b.Changes).ToList();
        }
    }

    private static EntryRef Find(IndexGeneration snapshot, string name)
    {
        for (var s = 0; s < snapshot.SegmentCount; s++)
        {
            if (!snapshot.HasSegment(s))
            {
                continue;
            }

            var segment = snapshot.GetSegment(s);
            for (var i = 0; i < snapshot.Count(s); i++)
            {
                if (!segment.IsDeleted(i) && segment.GetName(i).SequenceEqual(name))
                {
                    return new EntryRef(s, i);
                }
            }
        }

        throw new InvalidOperationException($"'{name}' not found.");
    }

    private static List<string> LiveNames(IndexGeneration snapshot)
    {
        var segment = snapshot.GetSegment(0);
        return Enumerable.Range(1, snapshot.Count(0) - 1).Where(i => !segment.IsDeleted(i)).Select(i => segment.GetName(i).ToString()).ToList();
    }

    private static IEnumerable<EntryRef> Scan(IndexGeneration snapshot, string folded)
    {
        var segment = snapshot.GetSegment(0);
        for (var i = 1; i < snapshot.Count(0); i++)
        {
            if (!segment.IsDeleted(i) && segment.GetFoldedName(i).Contains(folded, StringComparison.Ordinal))
            {
                yield return new EntryRef(0, i);
            }
        }
    }
}
