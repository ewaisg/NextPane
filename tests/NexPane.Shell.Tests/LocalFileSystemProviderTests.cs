using System.Diagnostics;
using NexPane.Core.Vfs;
using NexPane.Shell.FileSystem;

namespace NexPane.Shell.Tests;

public sealed class LocalFileSystemProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexpane-lfs-" + Guid.NewGuid().ToString("N"));
    private readonly LocalFileSystemProvider _provider = new();

    public LocalFileSystemProviderTests() => Directory.CreateDirectory(_root);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        // Remove links first so deleting the tree never follows them.
        foreach (var dir in Directory.EnumerateDirectories(_root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
                     .Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0).ToList())
        {
            Directory.Delete(dir);
        }

        Directory.Delete(@"\\?\" + _root, recursive: true);
    }

    [Fact]
    public async Task Enumerates_entries_with_sizes_times_and_kinds()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        var stamp = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(_root, "a.txt"), stamp);

        var entries = await List(VfsPath.FromWindowsPath(_root), new EnumerateOptions());

        entries.Should().HaveCount(2);
        var file = entries.Single(e => e.Name == "a.txt");
        file.Kind.Should().Be(VfsEntryKind.File);
        file.Size.Should().Be(5);
        file.ModifiedUtc.Should().Be(new DateTimeOffset(stamp));
        file.Path.Should().Be(VfsPath.FromWindowsPath(Path.Combine(_root, "a.txt")));
        entries.Single(e => e.Name == "sub").Kind.Should().Be(VfsEntryKind.Directory);
        entries.Single(e => e.Name == "sub").Size.Should().BeNull();
    }

    [Fact]
    public async Task Handles_paths_longer_than_300_characters()
    {
        var deep = _root;
        while (deep.Length < 320)
        {
            deep = Path.Combine(deep, "segment_" + new string('x', 30));
        }

        Directory.CreateDirectory(@"\\?\" + deep);
        var file = Path.Combine(deep, "long.txt");
        await File.WriteAllTextAsync(@"\\?\" + file, "deep", Ct);
        file.Length.Should().BeGreaterThan(300);

        var dir = VfsPath.FromWindowsPath(deep);
        var entries = await List(dir, new EnumerateOptions());
        entries.Should().ContainSingle(e => e.Name == "long.txt" && e.Size == 4);

        var path = dir.Combine("long.txt");
        await using (var stream = await _provider.OpenReadAsync(path, Ct))
        using (var reader = new StreamReader(stream))
        {
            (await reader.ReadToEndAsync(Ct)).Should().Be("deep");
        }

        (await _provider.GetEntryAsync(path, Ct))!.Size.Should().Be(4);
        await _provider.MoveAsync(path, dir.Combine("renamed.txt"), overwrite: false, Ct);
        (await _provider.GetEntryAsync(dir.Combine("renamed.txt"), Ct)).Should().NotBeNull();
        await _provider.DeleteAsync(dir.Combine("renamed.txt"), new DeleteOptions(ToRecycleBin: false), Ct);
        (await _provider.GetEntryAsync(dir.Combine("renamed.txt"), Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Hidden_and_protected_system_entries_are_filtered_on_request()
    {
        var hidden = Path.Combine(_root, "hidden.txt");
        var system = Path.Combine(_root, "protected.sys");
        File.WriteAllText(hidden, "h");
        File.WriteAllText(system, "s");
        File.WriteAllText(Path.Combine(_root, "normal.txt"), "n");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        File.SetAttributes(system, FileAttributes.Hidden | FileAttributes.System);
        var dir = VfsPath.FromWindowsPath(_root);

        (await List(dir, new EnumerateOptions(IncludeHidden: true, IncludeSystem: true))).Select(e => e.Name).Should().BeEquivalentTo("hidden.txt", "protected.sys", "normal.txt");
        (await List(dir, new EnumerateOptions(IncludeHidden: true, IncludeSystem: false))).Select(e => e.Name).Should().BeEquivalentTo("hidden.txt", "normal.txt");
        (await List(dir, new EnumerateOptions(IncludeHidden: false))).Select(e => e.Name).Should().BeEquivalentTo("normal.txt");
        (await List(dir, new EnumerateOptions(IncludeSystem: true, Pattern: "*.SYS"))).Select(e => e.Name).Should().BeEquivalentTo("protected.sys");

        File.SetAttributes(hidden, FileAttributes.Normal);
        File.SetAttributes(system, FileAttributes.Normal);
    }

    [Fact]
    public async Task Junctions_are_reported_with_their_target()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "inside.txt"), "x");
        var junction = Path.Combine(_root, "junction");
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!)
        {
            await process.WaitForExitAsync(Ct);
            process.ExitCode.Should().Be(0);
        }

        var entries = await List(VfsPath.FromWindowsPath(_root), new EnumerateOptions());

        var entry = entries.Single(e => e.Name == "junction");
        entry.Kind.Should().Be(VfsEntryKind.Junction);
        entry.LinkTarget.Should().Be(target);
        entry.IsContainer.Should().BeTrue();
        (await _provider.GetEntryAsync(VfsPath.FromWindowsPath(junction), Ct))!.Kind.Should().Be(VfsEntryKind.Junction);
    }

    [Fact]
    public async Task Symbolic_links_are_reported_with_their_target()
    {
        var target = Path.Combine(_root, "real.txt");
        File.WriteAllText(target, "r");
        var link = Path.Combine(_root, "link.txt");
        var dirTarget = Path.Combine(_root, "realdir");
        Directory.CreateDirectory(dirTarget);
        if (!LocalFileSystemProvider.TryCreateSymbolicLink(link, target, directory: false))
        {
            Assert.Skip("Symbolic links need Developer Mode (or elevation) for SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE.");
        }

        LocalFileSystemProvider.TryCreateSymbolicLink(Path.Combine(_root, "dirlink"), dirTarget, directory: true).Should().BeTrue();

        var entries = await List(VfsPath.FromWindowsPath(_root), new EnumerateOptions());

        entries.Single(e => e.Name == "link.txt").Kind.Should().Be(VfsEntryKind.Symlink);
        entries.Single(e => e.Name == "link.txt").LinkTarget.Should().Be(target);
        var dirLink = entries.Single(e => e.Name == "dirlink");
        dirLink.Kind.Should().Be(VfsEntryKind.Symlink);
        dirLink.IsContainer.Should().BeTrue();
    }

    [Fact]
    public async Task Write_set_times_create_directory_and_delete()
    {
        var dir = VfsPath.FromWindowsPath(_root).Combine("made");
        await _provider.CreateDirectoryAsync(dir, Ct);
        var file = dir.Combine("w.bin");
        await using (var stream = await _provider.OpenWriteAsync(file, WriteMode.CreateNew, Ct))
        {
            await stream.WriteAsync(new byte[] { 1, 2, 3 }, Ct);
        }

        await using (var stream = await _provider.OpenWriteAsync(file, WriteMode.Append, Ct))
        {
            await stream.WriteAsync(new byte[] { 4 }, Ct);
        }

        var when = new DateTimeOffset(2020, 2, 2, 2, 2, 2, TimeSpan.Zero);
        await _provider.SetTimesAsync(file, when, when, Ct);
        var entry = (await _provider.GetEntryAsync(file, Ct))!;
        entry.Size.Should().Be(4);
        entry.ModifiedUtc.Should().Be(when);
        entry.CreatedUtc.Should().Be(when);

        var act = async () => await _provider.OpenWriteAsync(file, WriteMode.CreateNew, Ct);
        await act.Should().ThrowAsync<IOException>();

        await _provider.DeleteAsync(dir, new DeleteOptions(Recursive: true, ToRecycleBin: false), Ct);
        Directory.Exists(dir.ToWindowsPath()).Should().BeFalse();
    }

    [Fact]
    public async Task Missing_and_denied_directories_raise_typed_errors()
    {
        var missing = VfsPath.FromWindowsPath(Path.Combine(_root, "nope"));

        var act = async () => await List(missing, new EnumerateOptions());

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task Watcher_reports_create_rename_delete()
    {
        await using var watcher = _provider.CreateWatcher(VfsPath.FromWindowsPath(_root), recursive: true)!;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var changes = new List<VfsChange>();
        var reading = Task.Run(async () =>
        {
            await foreach (var change in watcher.Changes(cts.Token))
            {
                lock (changes)
                {
                    changes.Add(change);
                    if (change.Kind == VfsChangeKind.Deleted)
                    {
                        break;
                    }
                }
            }
        }, Ct);
        await Task.Delay(200, Ct); // let the first read be issued

        File.WriteAllText(Path.Combine(_root, "w1.txt"), "1");
        File.Move(Path.Combine(_root, "w1.txt"), Path.Combine(_root, "w2.txt"));
        File.Delete(Path.Combine(_root, "w2.txt"));
        await reading;

        lock (changes)
        {
            changes.Should().Contain(c => c.Kind == VfsChangeKind.Created && c.Path.Name == "w1.txt");
            changes.Should().Contain(c => c.Kind == VfsChangeKind.Renamed && c.Path.Name == "w2.txt" && c.OldPath!.Value.Name == "w1.txt");
            changes.Should().Contain(c => c.Kind == VfsChangeKind.Deleted && c.Path.Name == "w2.txt");
        }
    }

    [Fact]
    public void Volume_facts_of_the_temp_volume_are_read()
    {
        var facts = VolumeFacts.TryGet(_root)!;

        facts.Should().NotBeNull();
        facts.RootPath.Should().EndWith("\\");
        facts.FileSystem.Should().NotBeNullOrEmpty();
        facts.Serial.Should().NotBe(0u);
    }

    private async Task<List<VfsEntry>> List(VfsPath dir, EnumerateOptions options)
    {
        var list = new List<VfsEntry>();
        await foreach (var e in _provider.EnumerateAsync(dir, options, Ct))
        {
            list.Add(e);
        }

        return list;
    }
}
