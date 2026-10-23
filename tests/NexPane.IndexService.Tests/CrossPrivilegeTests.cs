using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using NexPane.Core.Indexing;
using NexPane.Indexing.Service;
using NexPane.Indexing.Sources;
using NexPane.Indexing.Store;

namespace NexPane.IndexService.Tests;

/// <summary>
/// P3.13/P3.14 acceptance across a real privilege boundary (§7.5, §18 "Elevated"). The elevated side installs the built
/// <c>NexPane.IndexService.exe</c> as a LocalSystem test service (<c>sc create</c>, test pipe name), mounts an NTFS
/// VHDX with a folder the user may not list, and starts this test executable again as a non-elevated "Basic User"
/// (<c>runas /trustlevel:0x20000</c>). That client verifies the server (SCM process id, LocalSystem, image), streams the
/// VHDX volume and follows its journal; the elevated side then checks that nothing below the denied folder reached the
/// client, in the initial listing or live. The service and the VHDX are removed in <c>finally</c>.
/// </summary>
[Trait("Category", "Elevated")]
public sealed class CrossPrivilegeTests
{
    private static readonly string Exchange = Path.Combine(Path.GetTempPath(), "nexpane-xpriv");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Non_elevated_client_streams_a_vhdx_volume_from_the_installed_service_without_denied_subtrees()
    {
        Assert.SkipUnless(IsElevated(), "Run from an elevated terminal (eng\\run-elevated-tests.ps1): installs a test service.");
        var nonce = Guid.NewGuid().ToString("N");
        var serviceName = "NexPaneIndexTest" + nonce[..8];
        var pipeName = "NexPane.Index.test." + nonce;
        var exe = ServiceExecutable();
        var testExe = Environment.ProcessPath!;
        Assert.False(exe.Contains(' ', StringComparison.Ordinal) || testExe.Contains(' ', StringComparison.Ordinal),
            "runas /trustlevel needs paths without spaces");
        Directory.CreateDirectory(Exchange);
        using var vhd = TestVhd.Create();
        var root = vhd.Root;
        var deep = Directory.CreateDirectory(Path.Combine(root, "private", "a", "b"));
        File.WriteAllText(Path.Combine(deep.FullName, "secret-initial.txt"), "s");
        Directory.CreateDirectory(Path.Combine(root, "public"));
        File.WriteAllText(Path.Combine(root, "public", "visible-initial.txt"), "v");
        var denied = new DirectoryInfo(Path.Combine(root, "private"));
        var security = denied.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
        denied.SetAccessControl(security);

        var request = new ClientRequest(nonce, pipeName, serviceName, root, Path.Combine(Exchange, nonce + ".ready"), Path.Combine(Exchange, nonce + ".go"),
            Path.Combine(Exchange, nonce + ".result.json"));
        var requestPath = Path.Combine(Exchange, nonce + ".request.json");
        try
        {
            Run("sc.exe", $"create {serviceName} binPath= \"\\\"{exe}\\\" --pipe {pipeName}\" start= demand obj= LocalSystem");
            Run("sc.exe", $"start {serviceName}");
            await WaitFor(() => ServiceRunning(serviceName), TimeSpan.FromSeconds(30));

            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, Json), Ct);
            Run("runas.exe", $"/trustlevel:0x20000 \"{testExe} -method *{nameof(Client_side_of_the_cross_privilege_test)}\""); // xunit native runner syntax (the exe outside dotnet test)

            await WaitFor(() => File.Exists(request.ReadyPath), TimeSpan.FromSeconds(120));
            File.WriteAllText(Path.Combine(deep.FullName, "secret-live.txt"), "s");
            Directory.CreateDirectory(Path.Combine(deep.FullName, "c"));
            File.WriteAllText(Path.Combine(root, "public", "visible-live.txt"), "v");
            await File.WriteAllTextAsync(request.GoPath, "go", Ct);
            await WaitFor(() => File.Exists(request.ResultPath), TimeSpan.FromSeconds(60));

            var result = JsonSerializer.Deserialize<ClientResult>(await File.ReadAllTextAsync(request.ResultPath, Ct))!;
            result.Error.Should().BeNull();
            result.Elevated.Should().BeFalse("the client must run with a non-elevated token");
            result.Probe.Should().Be(nameof(IndexServiceState.Running), "the client verified the installed LocalSystem service");
            result.Initial.Should().Contain(["private", "public", "visible-initial.txt"]);
            result.Initial.Should().NotContain(["a", "b", "secret-initial.txt"]);
            result.Live.Should().Contain("visible-live.txt");
            result.Live.Should().NotContain(["secret-live.txt", "c", "a", "b", "secret-initial.txt"]);
            TestContext.Current.SendDiagnosticMessage(
                $"Cross-privilege: service {serviceName} (LocalSystem), client {result.User} (elevated: {result.Elevated}), {result.Initial.Count} initial / {result.Live.Count} live names, none below the denied folder");
        }
        finally
        {
            TryRun("sc.exe", $"stop {serviceName}");
            await WaitFor(() => !ServiceRunning(serviceName), TimeSpan.FromSeconds(20), assert: false);
            TryRun("sc.exe", $"delete {serviceName}");
            foreach (var file in Directory.EnumerateFiles(Exchange, nonce + "*"))
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>
    /// The non-elevated half, started by the test above through <c>runas /trustlevel</c>. Without a pending request
    /// written by an elevated run (or when itself elevated) it skips, so it never runs on its own.
    /// </summary>
    [Fact]
    public async Task Client_side_of_the_cross_privilege_test()
    {
        var requestPath = Directory.Exists(Exchange) ? Directory.EnumerateFiles(Exchange, "*.request.json").FirstOrDefault() : null;
        Assert.SkipWhen(requestPath is null || IsElevated(), "Started only by the elevated cross-privilege test as a non-elevated process.");
        var request = JsonSerializer.Deserialize<ClientRequest>(await File.ReadAllTextAsync(requestPath!, Ct))!;
        File.Delete(requestPath!);
        var result = new ClientResult { User = WindowsIdentity.GetCurrent().Name, Elevated = IsElevated() };
        try
        {
            var client = new IndexServiceClient(request.PipeName, TimeSpan.FromSeconds(10), request.ServiceName); // default server verification
            result.Probe = (await client.ProbeAsync(Ct)).ToString();
            await using var connection = await client.ConnectAsync(Ct);
            var volume = (await connection.ListVolumesAsync(Ct)).Single(v => v.Root.Equals(request.Root, StringComparison.OrdinalIgnoreCase));
            await using var store = new IndexStore();
            var segment = await store.RegisterVolumeAsync(new IndexVolumeInfo(volume.Root, VolumeKind.NtfsFixed, VolumeSerial: volume.Serial), volume.RootFileReference, Ct);
            var next = await connection.EnumerateIntoAsync(volume.VolumeGuid, store, segment, Ct);
            result.Initial = Names(store, segment);

            await using var follow = await client.ConnectAsync(Ct);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var following = follow.FollowAsync(volume.VolumeGuid, next, store, segment, null, stop.Token);
            await File.WriteAllTextAsync(request.ReadyPath, "ready", Ct);
            await WaitFor(() => File.Exists(request.GoPath), TimeSpan.FromSeconds(60));
            await WaitFor(() => Names(store, segment).Contains("visible-live.txt"), TimeSpan.FromSeconds(20), assert: false);
            await Task.Delay(TimeSpan.FromSeconds(1), Ct); // later journal polls would have delivered a leak by now
            result.Live = Names(store, segment);
            await stop.CancelAsync();
            await following.ContinueWith(_ => { }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            result.Error = ex.ToString();
        }

        await File.WriteAllTextAsync(request.ResultPath, JsonSerializer.Serialize(result, Json), Ct);
    }

    private static List<string> Names(IndexStore store, SegmentHandle segment)
    {
        var g = store.CurrentGeneration;
        var s = g.GetSegment(segment.Id);
        return [.. Enumerable.Range(1, g.Count(segment.Id) - 1).Where(i => !s.IsDeleted(i)).Select(i => s.GetName(i).ToString())];
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool ServiceRunning(string name) => NexPane.Shell.Security.ServerIdentity.TryQueryService(name) is { Running: true };

    private static string ServiceExecutable()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NexPane.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? "Debug" : "Release";
        return Directory.EnumerateFiles(Path.Combine(dir.FullName, "src", "NexPane.IndexService", "bin", configuration), "NexPane.IndexService.exe", SearchOption.AllDirectories)
            .Single(p => p.Contains(@"\win-x64\", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task WaitFor(Func<bool> condition, TimeSpan timeout, bool assert = true)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        if (assert)
        {
            condition().Should().BeTrue();
        }
    }

    private static void Run(string file, string arguments)
    {
        var (exit, output) = Execute(file, arguments);
        exit.Should().Be(0, "{0} {1} failed: {2}", file, arguments, output);
    }

    private static void TryRun(string file, string arguments) => Execute(file, arguments);

    private static (int Exit, string Output) Execute(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    private sealed record ClientRequest(string Nonce, string PipeName, string ServiceName, string Root, string ReadyPath, string GoPath, string ResultPath);

    private sealed class ClientResult
    {
        public string? User { get; set; }

        public bool Elevated { get; set; }

        public string? Probe { get; set; }

        public List<string> Initial { get; set; } = [];

        public List<string> Live { get; set; } = [];

        public string? Error { get; set; }
    }

    /// <summary>256 MB NTFS VHDX created, formatted and mounted with diskpart.</summary>
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
            var file = Path.Combine(Path.GetTempPath(), "nexpane-xpriv-" + Guid.NewGuid().ToString("N") + ".vhdx");
            var letter = Enumerable.Range('M', 'Z' - 'M' + 1).Select(c => (char)c).First(c => !Directory.Exists(c + ":\\"));
            Diskpart($"create vdisk file=\"{file}\" maximum=256 type=expandable\nselect vdisk file=\"{file}\"\nattach vdisk\ncreate partition primary\nformat fs=ntfs quick label=NexPaneXp\nassign letter={letter}\n");
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
