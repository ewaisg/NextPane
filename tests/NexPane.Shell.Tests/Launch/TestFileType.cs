using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NexPane.Shell.Tests.Launch;

/// <summary>
/// A temporary per-user file type: <c>HKCU\Software\Classes\.nxp&lt;id&gt;</c> → ProgID <c>NexPane.Test.&lt;id&gt;</c> with
/// verbs whose commands write marker files through cmd.exe. Never touches an existing association (e.g. <c>.txt</c>);
/// both keys are deleted on dispose and the shell is notified of the association change both times.
/// </summary>
internal sealed class TestFileType : IDisposable
{
    public const string DefaultVerb = "nexpanemark";
    public const string AltVerb = "nexpanealt";
    public const string CwdVerb = "nexpanecwd";

    private const string ClassesRoot = @"Software\Classes\";
    private const int ShcneAssocChanged = 0x08000000;

    public TestFileType()
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        Extension = ".nxp" + id;
        ProgId = "NexPane.Test." + id;
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        using (var ext = Registry.CurrentUser.CreateSubKey(ClassesRoot + Extension))
        {
            ext.SetValue(string.Empty, ProgId);
        }

        using (var prog = Registry.CurrentUser.CreateSubKey(ClassesRoot + ProgId))
        {
            prog.SetValue(string.Empty, "NexPane launch test file");
            using var shell = prog.CreateSubKey("shell");
            shell.SetValue(string.Empty, DefaultVerb);
            AddVerb(shell, DefaultVerb, $"\"{cmd}\" /d /c echo default>\"%1.default.marker\"");
            AddVerb(shell, AltVerb, $"\"{cmd}\" /d /c echo alt>\"%1.alt.marker\"");
            AddVerb(shell, CwdVerb, $"\"{cmd}\" /d /c cd>\"%1.cwd.marker\"");
        }

        NotifyAssociationsChanged();
    }

    public string Extension { get; }

    public string ProgId { get; }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(ClassesRoot + Extension, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(ClassesRoot + ProgId, throwOnMissingSubKey: false);
        NotifyAssociationsChanged();
    }

    /// <summary>Waits up to 15 s for a file to appear (the verb's process runs asynchronously to the launch).</summary>
    public static async Task<bool> WaitForFileAsync(Func<bool> exists, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (exists())
            {
                return true;
            }

            await Task.Delay(50, ct);
        }

        return exists();
    }

    public static void NotifyAssociationsChanged() => SHChangeNotify(ShcneAssocChanged, 0, 0, 0);

    private static void AddVerb(RegistryKey shell, string verb, string command)
    {
        using var verbKey = shell.CreateSubKey(verb);
        using var commandKey = verbKey.CreateSubKey("command");
        commandKey.SetValue(string.Empty, command);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
