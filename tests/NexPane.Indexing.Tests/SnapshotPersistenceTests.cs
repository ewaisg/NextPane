using System.Diagnostics;
using NexPane.Core.Indexing;
using NexPane.Indexing.Store;
using NexPane.Storage;

namespace NexPane.Indexing.Tests;

public sealed class SnapshotPersistenceTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexpane-snap-" + Guid.NewGuid().ToString("N"));
    private SqliteConnectionFactory _factory = null!;
    private SqliteWriter _writer = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _factory = new SqliteConnectionFactory(Path.Combine(_dir, "index.db"), DatabaseKind.Index);
        DatabaseBootstrapper.Open(_factory);
        _writer = new SqliteWriter(_factory);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        SqliteConnectionFactory.ClearPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Incremental_changes_round_trip_including_removals_and_renames()
    {
        await using (var store = new IndexStore())
        {
            var h = await store.RegisterVolumeAsync(new IndexVolumeInfo(@"E:\", VolumeKind.NtfsFixed, VolumeSerial: 0xABCD, FileSystem: "NTFS"), 5, Ct);
            await using var persistence = new IndexSnapshotPersistence(store, _factory, _writer);
            await store.SubmitAsync(h.Id, [IndexDelta.UpsertById(10, 5, "dir", 0x10, 0, 1, 2), IndexDelta.UpsertById(11, 10, "a.txt", 0x20, 7, 3, 4), IndexDelta.UpsertById(12, 10, "b.txt", 0x20, 8, 3, 4)], Ct);
            await store.FlushAsync(Ct);
            await persistence.FlushAsync(full: false, Ct);

            await store.SubmitAsync(h.Id, [IndexDelta.RemoveById(12), IndexDelta.MoveById(11, 5, "renamed.txt")], Ct);
            await store.FlushAsync(Ct);
            await persistence.FlushAsync(full: false, Ct);
            await persistence.UpdateVolumeStateAsync(@"E:\", VolumeSourceKind.UsnService, VolumeScanState.Complete, 77, 12345, null, Ct);
        }

        await using var reloaded = new IndexStore();
        await using var loader = new IndexSnapshotPersistence(reloaded, _factory, _writer);
        var volumes = await loader.LoadAsync(Ct);

        volumes.Should().ContainSingle();
        var (volume, handle) = volumes[0];
        volume.Info.VolumeSerial.Should().Be(0xABCDUL);
        volume.Info.FileSystem.Should().Be("NTFS");
        volume.RootFileId.Should().Be(5UL);
        volume.UsnJournalId.Should().Be(77);
        volume.LastUsn.Should().Be(12345);
        volume.ScanState.Should().Be(VolumeScanState.Complete);
        volume.EntryCount.Should().Be(2);
        var snapshot = reloaded.CurrentGeneration;
        var paths = Enumerable.Range(1, snapshot.Count(handle.Id) - 1).Select(i => new EntryRef(handle.Id, i))
            .Where(snapshot.IsLive).Select(snapshot.GetFullPath).ToList();
        paths.Should().BeEquivalentTo(@"E:\dir", @"E:\renamed.txt");
        var file = Enumerable.Range(1, snapshot.Count(handle.Id) - 1).Select(i => new EntryRef(handle.Id, i)).Single(e => snapshot.GetName(e).SequenceEqual("renamed.txt"));
        snapshot.Get(file).Should().Be(new IndexEntry(0, IndexEntryState.None, 0x20, 7, 3, 4, 11));
    }

    [Fact]
    public async Task One_million_entries_round_trip_and_load_time_is_reported()
    {
        await using (var store = new IndexStore())
        {
            var h = await store.RegisterVolumeAsync(new IndexVolumeInfo(@"D:\", VolumeKind.NtfsFixed), 5, Ct);
            foreach (var chunk in IndexStoreTests.SyntheticTree(1_000_000, 5).Chunk(8192))
            {
                await store.SubmitAsync(h.Id, chunk, Ct);
            }

            await store.FlushAsync(Ct);
            await using var persistence = new IndexSnapshotPersistence(store, _factory, _writer);
            await persistence.FlushAsync(full: true, Ct);
        }

        SqliteConnectionFactory.ClearPools();
        await using var reloaded = new IndexStore();
        await using var loader = new IndexSnapshotPersistence(reloaded, _factory, _writer);
        var watch = Stopwatch.StartNew();
        var loaded = await loader.LoadAsync(Ct);
        watch.Stop();

        var handle = loaded.Single().Handle;
        var snapshot = reloaded.CurrentGeneration;
        snapshot.Count(handle.Id).Should().Be(1_000_001);
        var segment = snapshot.GetSegment(handle.Id);
        Enumerable.Range(1, 1_000_000).Count(segment.IsDeleted).Should().Be(0);

        // Every entry's path must equal the path built from the generator (spot check 2 000 entries by file id).
        var expected = new IndexStoreTests.ExpectedTree(IndexStoreTests.SyntheticTree(1_000_000, 5), @"D:\");
        var byId = Enumerable.Range(1, 1_000_000).ToDictionary(i => segment.GetFileId(i));
        foreach (var id in expected.Sample(2_000, seed: 3))
        {
            segment.GetFullPath(byId[id]).Should().Be(expected.PathOf(id));
        }

        TestContext.Current.SendDiagnosticMessage($"Snapshot load of 1M entries: {watch.ElapsedMilliseconds} ms (budget 2000 ms)");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the precise 2 s budget is measured by the benchmark project; this guards gross regressions");
    }

    [Fact]
    public async Task Periodic_flush_writes_without_explicit_calls()
    {
        await using var store = new IndexStore();
        var h = await store.RegisterVolumeAsync(new IndexVolumeInfo(@"F:\", VolumeKind.OtherLocal), 1, Ct);
        await using var persistence = new IndexSnapshotPersistence(store, _factory, _writer, TimeSpan.FromMilliseconds(100));
        persistence.Start();

        await store.SubmitAsync(h.Id, [IndexDelta.UpsertById(2, 1, "x.bin", 0x20, 1, 1, 1)], Ct);
        await store.FlushAsync(Ct);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (persistence.FlushCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        persistence.FlushCount.Should().BeGreaterThan(0);
        (await persistence.ReadVolumesAsync(Ct)).Single().EntryCount.Should().Be(1);
    }
}
