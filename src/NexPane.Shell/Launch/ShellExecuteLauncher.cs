using System.ComponentModel;
using System.Runtime.InteropServices;
using NexPane.Shell.Threading;

namespace NexPane.Shell.Launch;

/// <summary>
/// <see cref="IShellLauncher"/> over <c>ShellExecuteExW</c> (ARCHITECTURE §11 "Launch"): the parsing name is turned into
/// an absolute PIDL with <c>SHParseDisplayName</c> and invoked with <c>SEE_MASK_INVOKEIDLIST | SEE_MASK_NOASYNC |
/// SEE_MASK_FLAG_LOG_USAGE</c> on a <see cref="ShellStaScheduler"/> thread (HANDOFF gotcha 7). Shell parsing names
/// such as <c>::{CLSID}</c> work as well as file-system paths.
/// </summary>
internal sealed class ShellExecuteLauncher : IShellLauncher
{
    /// <summary>Shell APIs take at most MAX_PATH (260) characters including the terminator (HANDOFF gotcha 6).</summary>
    internal const int MaxShellPath = 259;

    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    private readonly ShellStaScheduler _sta;

    public ShellExecuteLauncher(ShellStaScheduler sta)
    {
        ArgumentNullException.ThrowIfNull(sta);
        _sta = sta;
    }

    public Task<ShellLaunchResult> OpenAsync(string parsingName, string? verb, nint ownerHwnd) =>
        LaunchAsync(new ShellLaunchRequest(parsingName, verb) { OwnerHwnd = ownerHwnd }, CancellationToken.None);

    public Task<ShellLaunchResult> LaunchAsync(ShellLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ParsingName, nameof(request));
        return _sta.RunAsync(() => Execute(request), cancellationToken);
    }

    /// <summary>Must run on an STA thread with a message pump.</summary>
    internal static unsafe ShellLaunchResult Execute(ShellLaunchRequest request)
    {
        var name = ToShellName(request.ParsingName, out var isFileSystem);
        if (name is null)
        {
            return ShellLaunchErrors.FromWin32(ShellLaunchErrors.ErrorFilenameExceedsRange);
        }

        var hr = ShellLaunchNative.SHParseDisplayName(name, 0, out var pidl, 0, out _);
        using (pidl)
        {
            if (hr < 0)
            {
                return ShellLaunchErrors.FromHResult(hr);
            }

            var workingDirectory = request.WorkingDirectory is { } requested
                ? ToShellName(requested, out _) ?? requested
                : isFileSystem ? ParentDirectory(name) : null;

            var mask = ShellLaunchNative.SeeMaskInvokeIdList | ShellLaunchNative.SeeMaskNoAsync
                | ShellLaunchNative.SeeMaskFlagLogUsage | ShellLaunchNative.SeeMaskUnicode;
            if (request.SuppressErrorUi)
            {
                mask |= ShellLaunchNative.SeeMaskFlagNoUi;
            }

            fixed (char* verb = request.Verb)
            fixed (char* parameters = request.Arguments)
            fixed (char* directory = workingDirectory)
            {
                var info = new ShellLaunchNative.ShellExecuteInfo
                {
                    Size = (uint)sizeof(ShellLaunchNative.ShellExecuteInfo),
                    Mask = mask,
                    Hwnd = request.OwnerHwnd,
                    Verb = string.IsNullOrEmpty(request.Verb) ? null : verb,
                    Parameters = parameters,
                    Directory = directory,
                    Show = ToShowCommand(request.Window),
                    IdList = pidl.DangerousGetHandle(),
                };

                if (ShellLaunchNative.ShellExecuteEx(ref info))
                {
                    return ShellLaunchResult.Success;
                }

                var error = Marshal.GetLastPInvokeError();
                if (error != 0)
                {
                    return ShellLaunchErrors.FromWin32(error);
                }

                var (status, code) = ShellLaunchErrors.FromSeErr((int)info.InstApp);
                return new ShellLaunchResult(status, code, new Win32Exception(code).Message);
            }
        }
    }

    /// <summary>
    /// Returns the name to hand to the shell: extended-length prefixes are removed when the path fits MAX_PATH; a longer
    /// file-system path is replaced by its 8.3 short form when the volume has one; otherwise null (path too long).
    /// </summary>
    internal static unsafe string? ToShellName(string parsingName, out bool isFileSystem)
    {
        var name = parsingName;
        if (name.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            name = @"\\" + name[ExtendedUncPrefix.Length..];
        }
        else if (name.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            name = name[ExtendedPrefix.Length..];
        }

        isFileSystem = Path.IsPathFullyQualified(name);
        if (name.Length <= MaxShellPath)
        {
            return name;
        }

        if (!isFileSystem)
        {
            return null;
        }

        var extended = name.StartsWith(@"\\", StringComparison.Ordinal) ? ExtendedUncPrefix + name[2..] : ExtendedPrefix + name;
        char* buffer = stackalloc char[MaxShellPath + ExtendedUncPrefix.Length + 1];
        var length = ShellLaunchNative.GetShortPathName(extended, buffer, (uint)(MaxShellPath + ExtendedUncPrefix.Length + 1));
        if (length == 0 || length > MaxShellPath + ExtendedUncPrefix.Length)
        {
            return null;
        }

        var shortName = new string(buffer, 0, (int)length);
        shortName = shortName.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase)
            ? @"\\" + shortName[ExtendedUncPrefix.Length..]
            : shortName.StartsWith(ExtendedPrefix, StringComparison.Ordinal) ? shortName[ExtendedPrefix.Length..] : shortName;
        return shortName.Length <= MaxShellPath ? shortName : null;
    }

    private static string? ParentDirectory(string fileSystemPath)
    {
        var parent = Path.GetDirectoryName(fileSystemPath.TrimEnd('\\'));
        return string.IsNullOrEmpty(parent) || !Directory.Exists(parent) ? null : parent;
    }

    private static int ToShowCommand(ShellLaunchWindow window) => window switch
    {
        ShellLaunchWindow.Minimized => ShellLaunchNative.SwShowMinimized,
        ShellLaunchWindow.Maximized => ShellLaunchNative.SwShowMaximized,
        ShellLaunchWindow.Hidden => ShellLaunchNative.SwHide,
        _ => ShellLaunchNative.SwShowNormal,
    };
}
