using System.Diagnostics;
using System.Runtime.InteropServices;
using NexPane.Core.Indexing;
using NexPane.Indexing.Sources;
using NexPane.Indexing.Store;

namespace NexPane.Indexing.Tests;

public sealed class WalkerAndWatcherTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexpane-walk-" + Guid.NewGuid().ToString("N"));
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
        Directory.Delete(@"\\?\" + _root, recursive: true);
    }

    [Fact]
    public async Task Walk_of_a_generated_tree_matches_get_childitem_and_reports_throughput()
    {
        var files = int.TryParse(Environment.GetEnvironmentVariable("NEXPANE_WALKER_TREE_FILES"), out var n) ? n : 200_000;
        GenerateTree(_root, files);
        File.SetAttributes(Path.Combine(_root, "d000", "f00000.txt"), FileAttributes.Hidden | FileAttributes.System);

        var segment = await _store.RegisterVolumeAsync(new IndexVolumeInfo(_root + "\\", VolumeKind.NtfsFixed), 5, Ct);
        var walker = new WalkerVolumeSource(_root);
        var cold = await walker.WalkFreshAsync(_store, segment, null, Ct);

        var expected = CountWithGetChildItem(_root);
        cold.Entries.Should().Be(expected, "the walker must see exactly what Get-ChildItem -Recurse -Force sees");
        cold.Errors.Should().Be(0);
        LiveCount(segment).Should().Be(expected);

        // Warm-cache throughput: walk again into a fresh segment.
        var second = await _store.RegisterVolumeAsync(new IndexVolumeInfo(_root + "\\copy", VolumeKind.NtfsFixed), 5, Ct);
        var warm = await new WalkerVolumeSource(_root).WalkFreshAsync(_store, second, null, Ct);
        warm.Entries.Should().Be(expected);
        TestContext.Current.SendDiagnosticMessage($"Walker: {warm.Entries:N0} entries in {warm.Elapsed.TotalMilliseconds:F0} ms = {warm.EntriesPerSecond:N0} entries/s warm (budget ≥ 50 000/s); cold {cold.EntriesPerSecond:N0}/s");
        Assert.SkipWhen(warm.EntriesPerSecond < 50_000 && OnBattery(), $"Throughput gate skipped on battery power (measured {warm.EntriesPerSecond:N0} entries/s).");
        warm.EntriesPerSecond.Should().BeGreaterThanOrEqualTo(50_000);
    }

    [Fact]
    public async Task Paths_and_stat_values_are_indexed_correctly()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a", "b"));
        File.WriteAllText(Path.Combine(_root, "a", "b", "c.txt"), "12345");
        var stamp = new DateTime(2023, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(_root, "a", "b", "c.txt"), stamp);
        var segment = await _store.RegisterVolumeAsync(new IndexVolumeInfo(_root + "\\", VolumeKind.NtfsFixed), 5, Ct);

        await new WalkerVolumeSource(_root).WalkFreshAsync(_store, segment, null, Ct);

        var snapshot = _store.CurrentGeneration;
        var file = Find(segment, "c.txt");
        snapshot.GetFullPath(file).Should().Be(Path.Combine(_root, "a", "b", "c.txt"));
        snapshot.Get(file).Size.Should().Be(5);
        snapshot.Get(file).ModifiedUtc.Should().Be(stamp.ToFileTimeUtc());
        snapshot.Get(Find(segment, "b")).IsDirectory.Should().BeTrue();
    }

    [Fact]
    public async Task Junctions_are_listed_but_not_descended()
    {
        Directory.CreateDirectory(Path.Combine(_root, "real"));
        File.WriteAllText(Path.Combine(_root, "real", "x.txt"), "x");
        RunCmd($"mklink /J \"{Path.Combine(_root, "link")}\" \"{Path.Combine(_root, "real")}\"");
        var segment = await _store.RegisterVolumeAsync(new IndexVolumeInfo(_root + "\\", VolumeKind.NtfsFixed), 5, Ct);

        var result = await new WalkerVolumeSource(_root).WalkFreshAsync(_store, segment, null, Ct);

        result.Entries.Should().Be(3, "real, real\\x.txt and the junction itself");
        (_store.CurrentGeneration.Get(Find(segment, "link")).Attributes & 0x400).Should().NotBe(0u);
        Directory.Delete(Path.Combine(_root, "link"));
    }

    [Fact]
    public async Task Catch_up_rewalk_keeps_ids_and_reconciles_changes()
    {
        Directory.CreateDirectory(Path.Combine(_root, "keep"));
        File.WriteAllText(Path.Combine(_root, "keep", "same.txt"), "1");
        File.WriteAllText(Path.Combine(_root, "gone.txt"), "2");
        var segment = await _store.RegisterVolumeAsync(new IndexVolumeInfo(_root + "\\", VolumeKind.NtfsFixed), 5, Ct);
        var walker = new WalkerVolumeSource(_root);
        await walker.WalkFreshAsync(_store, segment, null, Ct);
        var same = Find(segment, "same.txt");
        var id = _store.CurrentGeneration.Get(same).FileId;

        File.Delete(Path.Combine(_root, "gone.txt"));
        Directory.CreateDirectory(Path.Combine(_root, "new", "deeper"));
        File.WriteAllText(Path.Combine(_root, "new", "deeper", "n.txt"), "3");
        File.WriteAllText(Path.Combine(_root, "keep", "same.txt"), "1234");
        await walker.RewalkAsync(_store, segment, string.Empty, null, Ct);

        var snapshot = _store.CurrentGeneration;
        LiveNames(segment).Should().BeEquivalentTo("keep", "same.txt", "new", "deeper", "n.txt");
        snapshot.Get(same).FileId.Should().Be(id, "unchanged entries keep their identity");
        snapshot.Get(same).Size.Should().Be(4);
        _store.UnresolvedDeltas.Should().Be(0);
    }

    [Fact]
    public async Task Watcher_reflects_create_rename_delete_within_one_second()
    {
        var segment = await _store.RegisterVolumeAsync(new IndexVolumeInfo(_root + "\\", VolumeKind.NtfsFixed), 5, Ct);
        var walker = new WalkerVolumeSource(_root);
        await walker.WalkFreshAsync(_store, segment, null, Ct);
        await using var source = new RdcwChangeSource(_store, segment, walker);
        source.Start();
        await Task.Delay(100, Ct);

        var created = await TimeUntil(() => File.WriteAllText(Path.Combine(_root, "new.txt"), "hello"), () => TryFind(segment, "new.txt") is not null);
        var renamed = await TimeUntil(() => File.Move(Path.Combine(_root, "new.txt"), Path.Combine(_root, "renamed.txt")),
            () => TryFind(segment, "renamed.txt") is not null && TryFind(segment, "new.txt") is null);
        var movedDir = await TimeUntil(() =>
        {
            var outside = Path.Combine(Path.GetTempPath(), "nexpane-movein-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(outside, "inner"));
            File.WriteAllText(Path.Combine(outside, "inner", "carried.txt"), "c");
            Directory.Move(outside, Path.Combine(_root, "moved-in"));
        }, () => TryFind(segment, "carried.txt") is not null);
        var deleted = await TimeUntil(() => File.Delete(Path.Combine(_root, "renamed.txt")), () => TryFind(segment, "renamed.txt") is null);

        foreach (var (name, elapsed) in new[] { ("create", created), ("rename", renamed), ("move-in", movedDir), ("delete", deleted) })
        {
            elapsed.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(1), "{0} must be visible within 1 s", name);
        }

        _store.CurrentGeneration.Get(Find(segment, "carried.txt")).Size.Should().Be(1);
    }

    [Fact]
    public async Task Buffer_overflow_triggers_a_rewalk_and_the_final_index_is_correct()
    {
        var count = int.TryParse(Environment.GetEnvironmentVariable("NEXPANE_OVERFLOW_FILES"), out var n) ? n : 100_000;
        var segment = await _store.RegisterVolumeAsync(new IndexVolumeInfo(_root + "\\", VolumeKind.NtfsFixed), 5, Ct);
        var walker = new WalkerVolumeSource(_root);
        await walker.WalkFreshAsync(_store, segment, null, Ct);
        await using var source = new RdcwChangeSource(_store, segment, walker, bufferSize: 4096);
        source.Start();
        await Task.Delay(100, Ct);

        Parallel.For(0, count, new ParallelOptions { CancellationToken = Ct, MaxDegreeOfParallelism = 8 }, i =>
        {
            var dir = Path.Combine(_root, "burst" + (i % 50).ToString("D2", System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"f{i:D6}.bin"), []);
        });

        var expected = count + 50;
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while ((LiveCount(segment) != expected || source.OverflowRewalks == 0) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, Ct);
        }

        source.OverflowRewalks.Should().BeGreaterThan(0, "a 4 KB buffer cannot hold {0} notifications", count);
        LiveCount(segment).Should().Be(expected);
        LiveNames(segment).Where(x => x.StartsWith('f')).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Source_selection_covers_every_branch()
    {
        var ntfs = new VolumeCandidate(@"C:\", "NTFS", IsNetwork: false, IsRemovable: false, SupportsUsnJournal: true);

        (await Select(IndexServiceState.Running, false, ntfs)).Should().Be(new SourceDecision(VolumeSourceKind.UsnService, VolumeKind.NtfsFixed, "NTFS fast index"));
        (await Select(IndexServiceState.Running, true, ntfs)).Source.Should().Be(VolumeSourceKind.UsnService, "the service is preferred even when elevated");
        (await Select(IndexServiceState.NotInstalled, true, ntfs)).Source.Should().Be(VolumeSourceKind.UsnInProc);
        var walker = await Select(IndexServiceState.NotInstalled, false, ntfs);
        walker.Source.Should().Be(VolumeSourceKind.Walker);
        walker.Detail.Should().Be("Walker");
        walker.Hint.Should().Be(VolumeSourceSelector.ServiceMissingHint);
        (await Select(IndexServiceState.Stopped, false, ntfs)).Hint.Should().Be(VolumeSourceSelector.ServiceStoppedHint);
        (await Select(IndexServiceState.Outdated, false, ntfs)).Hint.Should().Be(VolumeSourceSelector.ServiceOutdatedHint);
        (await Select(IndexServiceState.Running, false, ntfs with { FileSystem = "exFAT" })).Should().Be(new SourceDecision(VolumeSourceKind.Walker, VolumeKind.OtherLocal, "Walker (exFAT)"));
        (await Select(IndexServiceState.Running, false, ntfs with { IsRemovable = true })).Kind.Should().Be(VolumeKind.Removable);
        (await Select(IndexServiceState.Running, true, ntfs with { RootPath = @"\\nas\share\", IsNetwork = true })).Source.Should().Be(VolumeSourceKind.Crawler);

        var status = walker.ToStatus(VolumeScanState.Complete, 42);
        status.Source.Should().Be(VolumeSourceKind.Walker);
        status.Detail.Should().Be("Walker");
        status.EntryCount.Should().Be(42);
    }

    private static Task<SourceDecision> Select(IndexServiceState state, bool elevated, VolumeCandidate volume) =>
        new VolumeSourceSelector(new ProbeDouble(state), new ElevationDouble(elevated)).SelectAsync(volume, Ct);

    private static void GenerateTree(string root, int files)
    {
        const int directories = 200;
        Parallel.For(0, directories, d =>
        {
            var dir = Path.Combine(root, $"d{d:D3}", d % 4 == 0 ? "nested" : string.Empty);
            Directory.CreateDirectory(dir);
            var perDir = files / directories;
            for (var f = 0; f < perDir; f++)
            {
                File.WriteAllBytes(Path.Combine(d % 4 == 0 && f == 0 ? Path.Combine(root, $"d{d:D3}") : dir, $"f{f:D5}.txt"), []);
            }
        });
    }

    private static long CountWithGetChildItem(string root)
    {
        var info = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -Command \"(Get-ChildItem -LiteralPath '{root}' -Recurse -Force | Measure-Object).Count\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return long.Parse(output.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void RunCmd(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + arguments) { CreateNoWindow = true, UseShellExecute = false })!;
        process.WaitForExit();
        process.ExitCode.Should().Be(0);
    }

    private static async Task<TimeSpan> TimeUntil(Action action, Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        action();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(5, Ct);
        }

        condition().Should().BeTrue();
        return watch.Elapsed;
    }

    private long LiveCount(SegmentHandle segment)
    {
        var snapshot = _store.CurrentGeneration;
        var s = snapshot.GetSegment(segment.Id);
        return Enumerable.Range(1, snapshot.Count(segment.Id) - 1).LongCount(i => !s.IsDeleted(i));
    }

    private List<string> LiveNames(SegmentHandle segment)
    {
        var snapshot = _store.CurrentGeneration;
        var s = snapshot.GetSegment(segment.Id);
        return Enumerable.Range(1, snapshot.Count(segment.Id) - 1).Where(i => !s.IsDeleted(i)).Select(i => s.GetName(i).ToString()).ToList();
    }

    private EntryRef Find(SegmentHandle segment, string name) =>
        TryFind(segment, name) ?? throw new InvalidOperationException($"'{name}' not indexed.");

    private EntryRef? TryFind(SegmentHandle segment, string name)
    {
        var snapshot = _store.CurrentGeneration;
        var s = snapshot.GetSegment(segment.Id);
        for (var i = 1; i < snapshot.Count(segment.Id); i++)
        {
            if (!s.IsDeleted(i) && s.GetName(i).SequenceEqual(name))
            {
                return new EntryRef(segment.Id, i);
            }
        }

        return null;
    }

    private static bool OnBattery() => GetSystemPowerStatus(out var status) && status.AcLineStatus == 0;

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    private sealed class ProbeDouble(IndexServiceState state) : IIndexServiceProbe
    {
        public Task<IndexServiceState> ProbeAsync(CancellationToken ct) => Task.FromResult(state);
    }

    private sealed class ElevationDouble(bool elevated) : IProcessElevation
    {
        public bool IsElevated => elevated;
    }
}
