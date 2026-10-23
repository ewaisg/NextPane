using System.Diagnostics;
using NexPane.Core.Indexing;
using NexPane.Indexing.Ntfs;
using NexPane.Indexing.Store;
using NexPane.Shell.FileSystem;

namespace NexPane.Indexing.Tests;

/// <summary>
/// Acceptance tests of P3.10–P3.12 on a freshly formatted NTFS VHDX created and mounted with diskpart (§18 "Elevated").
/// Run from an elevated terminal: <c>dotnet test --project tests/NexPane.Indexing.Tests -c Release --filter-trait "Category=Elevated"</c>.
/// </summary>
[Trait("Category", "Elevated")]
public sealed class ElevatedVolumeTests : IAsyncLifetime
{
    private TestVhd _vhd = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _vhd = TestVhd.Create();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _vhd.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Enumeration_of_10k_files_matches_and_journal_reflects_changes_within_one_second()
    {
        var root = _vhd.Root;
        Parallel.For(0, 10_000, i =>
        {
            var dir = Path.Combine(root, $"d{i % 100:D2}");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"f{i:D5}.bin"), new byte[i % 4096]);
        });

        var backend = new NtfsVolumeBackend();
        var volume = backend.ListVolumes().Single(v => v.Root.Equals(root, StringComparison.OrdinalIgnoreCase));
        await using var store = new IndexStore();
        var segment = await store.RegisterVolumeAsync(new IndexVolumeInfo(root, VolumeKind.NtfsFixed, VolumeSerial: volume.Serial), volume.RootFileReference, Ct);
        var source = new UsnVolumeSource(store, segment, volume);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var running = source.RunInProcessAsync(backend, new JournalPosition(null, null), null, cts.Token);

        await WaitFor(() => Count(store, segment, n => n.StartsWith('f')) == 10_000, TimeSpan.FromSeconds(30));

        // Parity with a directory enumeration (what the walker sees): no $MFT/$Extend metafiles, nothing the user cannot list.
        var snapshot = store.CurrentGeneration;
        var seg = snapshot.GetSegment(segment.Id);
        var indexedDirectory = Enumerable.Range(1, snapshot.Count(segment.Id) - 1).Where(i => !seg.IsDeleted(i))
            .ToDictionary(seg.GetFullPath, seg.IsDirectory, StringComparer.OrdinalIgnoreCase);
        var enumeratedDirectory = new DirectoryInfo(root).EnumerateFileSystemInfos("*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
        }).ToDictionary(i => i.FullName, i => (i.Attributes & FileAttributes.Directory) != 0, StringComparer.OrdinalIgnoreCase);

        // Set differences (cheap at 10k entries; BeEquivalentTo on large sets is quadratic), first 20 shown.
        var onlyIndexed = indexedDirectory.Keys.Except(enumeratedDirectory.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var onlyEnumerated = enumeratedDirectory.Keys.Except(indexedDirectory.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(onlyIndexed.Count == 0, $"{onlyIndexed.Count} indexed path(s) not in the directory enumeration: {string.Join(", ", onlyIndexed.Take(20))}");
        Assert.True(onlyEnumerated.Count == 0, $"{onlyEnumerated.Count} enumerated path(s) missing from the index: {string.Join(", ", onlyEnumerated.Take(20))}");

        // Directory/file classification of every entry matches the filesystem.
        var kindMismatches = indexedDirectory.Where(p => p.Value != enumeratedDirectory[p.Key])
            .Select(p => $"{p.Key} (indexed {(p.Value ? "directory" : "file")})").ToList();
        Assert.True(kindMismatches.Count == 0, $"{kindMismatches.Count} entr(ies) with the wrong directory flag: {string.Join(", ", kindMismatches.Take(20))}");

        // Sizes from the raw $MFT equal GetFileInformationByHandleEx (FileInfo) values.
        for (var i = 1; i < snapshot.Count(segment.Id); i += 97)
        {
            if (!seg.IsDeleted(i) && !seg.IsDirectory(i))
            {
                var info = new FileInfo(seg.GetFullPath(i));
                seg.GetSize(i).Should().Be(info.Length);
                seg.GetModified(i).Should().Be(info.LastWriteTimeUtc.ToFileTimeUtc());
            }
        }

        var watch = Stopwatch.StartNew();
        File.Move(Path.Combine(root, "d00", "f00000.bin"), Path.Combine(root, "d01", "renamed.bin"));
        File.Delete(Path.Combine(root, "d02", "f00002.bin"));
        await WaitFor(() => Count(store, segment, n => n == "renamed.bin") == 1 && Count(store, segment, n => n == "f00002.bin") == 0, TimeSpan.FromSeconds(5));
        watch.Elapsed.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(1));

        await cts.CancelAsync();
        await running.ContinueWith(_ => { }, CancellationToken.None);
    }

    [Fact]
    public async Task Deleting_the_journal_triggers_a_full_rescan_and_the_index_is_correct()
    {
        var root = _vhd.Root;
        File.WriteAllText(Path.Combine(root, "before.txt"), "1");
        var backend = new NtfsVolumeBackend();
        var volume = backend.ListVolumes().Single(v => v.Root.Equals(root, StringComparison.OrdinalIgnoreCase));
        var persisted = new JournalPosition(unchecked((long)volume.JournalId), volume.NextUsn);

        Run("fsutil", $"usn deletejournal /d {root.TrimEnd('\\')}");
        File.WriteAllText(Path.Combine(root, "after.txt"), "2");
        var recreated = backend.ListVolumes().Single(v => v.Root.Equals(root, StringComparison.OrdinalIgnoreCase));
        recreated.JournalId.Should().NotBe(volume.JournalId);

        await using var store = new IndexStore();
        var segment = await store.RegisterVolumeAsync(new IndexVolumeInfo(root, VolumeKind.NtfsFixed), recreated.RootFileReference, Ct);
        var source = new UsnVolumeSource(store, segment, recreated);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var running = source.RunInProcessAsync(backend, persisted, null, cts.Token);
        await WaitFor(() => Count(store, segment, n => n is "before.txt" or "after.txt") == 2, TimeSpan.FromSeconds(30));

        source.Decision!.Mode.Should().Be(JournalStartMode.FullScan);
        source.FullScans.Should().Be(1);
        await cts.CancelAsync();
        await running.ContinueWith(_ => { }, CancellationToken.None);
    }

    private static int Count(IndexStore store, SegmentHandle segment, Func<string, bool> name)
    {
        var g = store.CurrentGeneration;
        var s = g.GetSegment(segment.Id);
        return Enumerable.Range(1, g.Count(segment.Id) - 1).Count(i => !s.IsDeleted(i) && name(s.GetName(i).ToString()));
    }

    private static async Task WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        condition().Should().BeTrue();
    }

    internal static void Run(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, "{0} {1}", file, arguments);
    }

    /// <summary>256 MB NTFS VHDX created, formatted and mounted with diskpart (no Hyper-V module needed).</summary>
    private sealed class TestVhd : IDisposable
    {
        private readonly string _file;

        private TestVhd(string file, string root)
        {
            _file = file;
            Root = root;
        }

        public string Root { get; }

        public static TestVhd Create()
        {
            var file = Path.Combine(Path.GetTempPath(), "nexpane-usn-" + Guid.NewGuid().ToString("N") + ".vhdx");
            var letter = Enumerable.Range('M', 'Z' - 'M' + 1).Select(c => (char)c).First(c => !Directory.Exists(c + ":\\"));
            Diskpart($"create vdisk file=\"{file}\" maximum=256 type=expandable\nselect vdisk file=\"{file}\"\nattach vdisk\ncreate partition primary\nformat fs=ntfs quick label=NexPaneUsn\nassign letter={letter}\n");
            return new TestVhd(file, letter + ":\\");
        }

        public void Dispose()
        {
            Diskpart($"select vdisk file=\"{_file}\"\ndetach vdisk\n");
            File.Delete(_file);
        }

        private static void Diskpart(string script)
        {
            var path = Path.GetTempFileName();
            File.WriteAllText(path, script);
            try
            {
                Run("diskpart.exe", $"/s \"{path}\"");
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
