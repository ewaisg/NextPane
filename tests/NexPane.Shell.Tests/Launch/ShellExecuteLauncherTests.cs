using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexPane.Shell.Launch;
using NexPane.Shell.Threading;

namespace NexPane.Shell.Tests.Launch;

/// <summary>
/// P3.34 integration tests against the real shell. The acceptance text names ".txt"; the user's real <c>.txt</c>
/// association is never modified, so a unique per-run extension with test verbs is registered under
/// HKCU\Software\Classes instead (see <see cref="TestFileType"/>).
/// </summary>
public sealed class ShellExecuteLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NexPane.Launch." + Guid.NewGuid().ToString("N")[..8]);
    private readonly ShellStaScheduler _sta = new(2);
    private readonly ShellExecuteLauncher _launcher;

    public ShellExecuteLauncherTests()
    {
        Directory.CreateDirectory(_root);
        _launcher = new ShellExecuteLauncher(_sta);
    }

    public void Dispose()
    {
        _sta.Dispose();
        try
        {
            Directory.Delete(@"\\?\" + _root, recursive: true);
        }
        catch (IOException)
        {
            // A marker writer that is still exiting can hold the folder briefly; the temp folder is left behind then.
        }
    }

    [Fact]
    public async Task Default_verb_runs_the_registered_command_for_a_unicode_path_with_spaces()
    {
        using var type = new TestFileType();
        var file = CreateFile(Path.Combine(_root, "Ordner mit Leerzeichen ü 文件"), "Notiz ä 测试 file" + type.Extension);

        var result = await _launcher.LaunchAsync(Request(file, verb: null), TestContext.Current.CancellationToken);

        result.Should().Be(ShellLaunchResult.Success);
        (await TestFileType.WaitForFileAsync(() => File.Exists(file + ".default.marker"), TestContext.Current.CancellationToken))
            .Should().BeTrue("the default verb's command writes <file>.default.marker");
    }

    [Fact]
    public async Task Named_verb_runs_that_verb_and_not_the_default()
    {
        using var type = new TestFileType();
        var file = CreateFile(_root, "named" + type.Extension);

        var result = await _launcher.LaunchAsync(Request(file, TestFileType.AltVerb), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Message);
        (await TestFileType.WaitForFileAsync(() => File.Exists(file + ".alt.marker"), TestContext.Current.CancellationToken)).Should().BeTrue();
        File.Exists(file + ".default.marker").Should().BeFalse();
    }

    [Fact]
    public async Task OpenAsync_is_the_double_click_entry_point_and_uses_the_default_verb()
    {
        using var type = new TestFileType();
        var file = CreateFile(_root, "double click" + type.Extension);

        var result = await _launcher.OpenAsync(file, verb: null, ownerHwnd: 0);

        result.Succeeded.Should().BeTrue(result.Message);
        (await TestFileType.WaitForFileAsync(() => File.Exists(file + ".default.marker"), TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public async Task Working_directory_defaults_to_the_parent_folder_and_can_be_overridden()
    {
        using var type = new TestFileType();
        var parent = Path.Combine(_root, "parent dir");
        var other = Directory.CreateDirectory(Path.Combine(_root, "other dir")).FullName;
        var first = CreateFile(parent, "cwd1" + type.Extension);
        var second = CreateFile(parent, "cwd2" + type.Extension);
        var ct = TestContext.Current.CancellationToken;

        (await _launcher.LaunchAsync(Request(first, TestFileType.CwdVerb), ct)).Succeeded.Should().BeTrue();
        (await _launcher.LaunchAsync(Request(second, TestFileType.CwdVerb) with { WorkingDirectory = other }, ct)).Succeeded.Should().BeTrue();

        (await TestFileType.WaitForFileAsync(() => HasContent(first + ".cwd.marker"), ct)).Should().BeTrue();
        (await TestFileType.WaitForFileAsync(() => HasContent(second + ".cwd.marker"), ct)).Should().BeTrue();
        File.ReadAllText(first + ".cwd.marker").Trim().Should().BeEquivalentTo(parent);
        File.ReadAllText(second + ".cwd.marker").Trim().Should().BeEquivalentTo(other);
    }

    [Fact]
    public async Task Path_longer_than_MAX_PATH_launches_through_its_short_name_or_reports_PathTooLong()
    {
        using var type = new TestFileType();
        var directory = _root;
        while (directory.Length < 280)
        {
            directory = Path.Combine(directory, "a long directory name segment");
        }

        var file = CreateFile(directory, "long" + type.Extension);
        file.Length.Should().BeGreaterThan(ShellExecuteLauncher.MaxShellPath);

        var result = await _launcher.LaunchAsync(Request(file, verb: null), TestContext.Current.CancellationToken);

        if (ShellExecuteLauncher.ToShellName(file, out _) is null)
        {
            // 8.3 names disabled on this volume: the shell cannot address the item, and says so with a typed result.
            result.Status.Should().Be(ShellLaunchStatus.PathTooLong);
            return;
        }

        result.Succeeded.Should().BeTrue(result.Message);
        (await TestFileType.WaitForFileAsync(
            () => Directory.EnumerateFiles(@"\\?\" + directory, "*.default.marker").Any(), TestContext.Current.CancellationToken))
            .Should().BeTrue("the verb ran against the item's 8.3 path inside the long directory");
    }

    [Fact]
    public async Task Nonexistent_file_maps_to_FileNotFound()
    {
        var missing = Path.Combine(_root, "does not exist.txt");

        var result = await _launcher.LaunchAsync(Request(missing, verb: null), TestContext.Current.CancellationToken);

        result.Status.Should().Be(ShellLaunchStatus.FileNotFound);
        result.NativeError.Should().Be(ShellLaunchErrors.ErrorFileNotFound);
        result.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Nonexistent_folder_maps_to_FileNotFound_or_PathNotFound()
    {
        var missing = Path.Combine(_root, "no such folder", "file.txt");

        var result = await _launcher.LaunchAsync(Request(missing, verb: null), TestContext.Current.CancellationToken);

        result.Status.Should().BeOneOf(ShellLaunchStatus.FileNotFound, ShellLaunchStatus.PathNotFound);
    }

    [Fact]
    public async Task Unregistered_extension_without_error_UI_maps_to_NoAssociation()
    {
        // Not the default or "open" verb: for an unassociated type those belong to HKCR\Unknown and start the
        // "Pick an app" prompt (OpenWith.exe), which the shell reports as success. "print" has no handler at all.
        var file = CreateFile(_root, "orphan.nxpnoassoc" + Guid.NewGuid().ToString("N")[..8]);

        var result = await _launcher.LaunchAsync(Request(file, "print"), TestContext.Current.CancellationToken);

        result.Status.Should().Be(ShellLaunchStatus.NoAssociation);
        result.NativeError.Should().Be(ShellLaunchErrors.ErrorNoAssociation);
    }

    [Fact]
    public async Task Unknown_verb_on_a_registered_type_maps_to_NoAssociation()
    {
        using var type = new TestFileType();
        var file = CreateFile(_root, "verb" + type.Extension);

        var result = await _launcher.LaunchAsync(Request(file, "nexpanenosuchverb"), TestContext.Current.CancellationToken);

        result.Status.Should().Be(ShellLaunchStatus.NoAssociation);
    }

    [Fact]
    public async Task Cancelled_before_it_runs_returns_a_cancelled_task()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => _launcher.LaunchAsync(Request(Path.Combine(_root, "x.txt"), verb: null), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Execute_runs_on_a_Shell_STA_thread()
    {
        var apartment = await _sta.RunAsync(() => Thread.CurrentThread.GetApartmentState(), TestContext.Current.CancellationToken);

        apartment.Should().Be(ApartmentState.STA);
    }

    [Fact]
    public void Extended_length_prefixes_are_removed_for_the_shell()
    {
        ShellExecuteLauncher.ToShellName(@"\\?\C:\Users\x\file.txt", out var fs).Should().Be(@"C:\Users\x\file.txt");
        fs.Should().BeTrue();
        ShellExecuteLauncher.ToShellName(@"\\?\UNC\server\share\file.txt", out fs).Should().Be(@"\\server\share\file.txt");
        fs.Should().BeTrue();
        ShellExecuteLauncher.ToShellName("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", out fs).Should().Be("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}");
        fs.Should().BeFalse("CLSID parsing names are shell namespace items, not file-system paths");
        ShellExecuteLauncher.ToShellName("::" + new string('x', 300), out _).Should().BeNull();
    }

    [Fact]
    public async Task AddShell_registers_the_launcher_and_clamps_the_STA_pool_size()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ShellServiceCollectionExtensions.StaThreadCountKey] = "64" })
            .Build();
        await using var provider = new ServiceCollection().AddShell(configuration).BuildServiceProvider();

        provider.GetRequiredService<IShellLauncher>().Should().BeOfType<ShellExecuteLauncher>();
        provider.GetRequiredService<ShellStaScheduler>().MaximumConcurrencyLevel.Should().Be(ShellStaScheduler.MaxThreads);
    }

    private static ShellLaunchRequest Request(string path, string? verb) =>
        new(path, verb) { Window = ShellLaunchWindow.Hidden, SuppressErrorUi = true };

    private static string CreateFile(string directory, string name)
    {
        Directory.CreateDirectory(@"\\?\" + directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(@"\\?\" + path, "NexPane launch test");
        return path;
    }

    private static bool HasContent(string path)
    {
        try
        {
            return File.Exists(path) && File.ReadAllText(path).Trim().Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
