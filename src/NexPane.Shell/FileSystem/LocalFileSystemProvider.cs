using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NexPane.Core.Vfs;

namespace NexPane.Shell.FileSystem;

/// <summary>
/// <c>file:</c> provider (P3.02) for local drives and UNC shares: native <c>FindFirstFileExW</c> listings over
/// <c>\\?\</c> long paths, hidden/system filtering, symbolic link and junction kinds with their targets, and an
/// <see cref="RdcwWatcher"/>-based change watcher.
/// </summary>
internal sealed class LocalFileSystemProvider : IFileSystemProvider
{
    public string Scheme => VfsPath.FileScheme;

    public VfsCapabilities GetCapabilities(VfsPath root) =>
        VfsCapabilities.Read | VfsCapabilities.Write | VfsCapabilities.Delete | VfsCapabilities.Rename | VfsCapabilities.CreateDirectory |
        VfsCapabilities.Watch | VfsCapabilities.SetTimes | VfsCapabilities.RandomAccessRead | VfsCapabilities.AlternateStreams |
        VfsCapabilities.ShellNamespace;

    public async IAsyncEnumerable<VfsEntry> EnumerateAsync(VfsPath directory, EnumerateOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var path = directory.ToWindowsPath();
        var entries = new List<NativeEntry>();
        var error = await Task.Run(() => NativeDirectory.Enumerate(path, entries), ct).ConfigureAwait(false);
        if (error != 0)
        {
            throw ToException(error, path);
        }

        var pattern = string.IsNullOrEmpty(options.Pattern) ? null : options.Pattern.ToUpperInvariant();
        foreach (var native in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!options.IncludeHidden && (native.Attributes & FileAttributes.Hidden) != 0)
            {
                continue;
            }

            if (!options.IncludeSystem && (native.Attributes & FileAttributes.System) != 0 && (native.Attributes & FileAttributes.Hidden) != 0)
            {
                continue; // protected operating-system files (hidden + system), like Explorer's default
            }

            if (pattern is not null && !WildcardMatch(pattern, native.Name.ToUpperInvariant()))
            {
                continue;
            }

            yield return ToEntry(directory.Combine(native.Name), path, native);
        }
    }

    public ValueTask<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken ct)
    {
        var windows = path.ToWindowsPath();
        FileSystemInfo info = Directory.Exists(windows) ? new DirectoryInfo(windows) : new FileInfo(windows);
        if (!info.Exists)
        {
            return ValueTask.FromResult<VfsEntry?>(null);
        }

        var kind = KindOf(info.Attributes, info.LinkTarget is not null, IsJunction(info));
        var size = info is FileInfo f && kind == VfsEntryKind.File ? f.Length : (long?)null;
        return ValueTask.FromResult<VfsEntry?>(new VfsEntry(path, path.Name, kind, size,
            new DateTimeOffset(info.CreationTimeUtc), new DateTimeOffset(info.LastWriteTimeUtc), info.Attributes, info.LinkTarget));
    }

    public ValueTask<Stream> OpenReadAsync(VfsPath path, CancellationToken ct) =>
        ValueTask.FromResult<Stream>(new FileStream(path.ToWindowsPath(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous));

    public ValueTask<Stream> OpenWriteAsync(VfsPath path, WriteMode mode, CancellationToken ct)
    {
        var fileMode = mode switch
        {
            WriteMode.CreateNew => FileMode.CreateNew,
            WriteMode.Overwrite => FileMode.Create,
            WriteMode.Append => FileMode.Append,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown write mode."),
        };
        return ValueTask.FromResult<Stream>(new FileStream(path.ToWindowsPath(), fileMode, FileAccess.Write, FileShare.Read, 81920, FileOptions.Asynchronous));
    }

    public ValueTask CreateDirectoryAsync(VfsPath path, CancellationToken ct)
    {
        Directory.CreateDirectory(path.ToWindowsPath());
        return ValueTask.CompletedTask;
    }

    public async ValueTask DeleteAsync(VfsPath path, DeleteOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var windows = path.ToWindowsPath();
        if (options.ToRecycleBin)
        {
            await RecycleBin.SendAsync(windows, ct).ConfigureAwait(false);
            return;
        }

        var attributes = File.GetAttributes(windows);
        if ((attributes & FileAttributes.Directory) == 0)
        {
            File.Delete(windows);
        }
        else if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(windows, recursive: false); // removes the link, never the target's contents
        }
        else
        {
            Directory.Delete(windows, options.Recursive);
        }
    }

    public ValueTask MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken ct)
    {
        var from = source.ToWindowsPath();
        var to = destination.ToWindowsPath();
        if (File.Exists(from))
        {
            File.Move(from, to, overwrite);
            return ValueTask.CompletedTask;
        }

        if (overwrite && Directory.Exists(to))
        {
            Directory.Delete(to, recursive: true);
        }

        if (string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(from, to);
        }
        else
        {
            CopyDirectory(from, to, ct);
            Directory.Delete(from, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask SetTimesAsync(VfsPath path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct)
    {
        var windows = path.ToWindowsPath();
        var isDirectory = Directory.Exists(windows);
        if (created is { } c)
        {
            if (isDirectory)
            {
                Directory.SetCreationTimeUtc(windows, c.UtcDateTime);
            }
            else
            {
                File.SetCreationTimeUtc(windows, c.UtcDateTime);
            }
        }

        if (modified is { } m)
        {
            if (isDirectory)
            {
                Directory.SetLastWriteTimeUtc(windows, m.UtcDateTime);
            }
            else
            {
                File.SetLastWriteTimeUtc(windows, m.UtcDateTime);
            }
        }

        return ValueTask.CompletedTask;
    }

    public IVfsWatcher? CreateWatcher(VfsPath directory, bool recursive) => new RdcwVfsWatcher(directory, recursive);

    /// <summary>Creates a symbolic link without elevation when Developer Mode allows it. Returns false when the
    /// privilege is not held (Developer Mode off and not elevated).</summary>
    public static bool TryCreateSymbolicLink(string linkPath, string targetPath, bool directory)
    {
        var flags = Win32FileApi.SymbolicLinkFlagAllowUnprivilegedCreate | (directory ? Win32FileApi.SymbolicLinkFlagDirectory : 0);
        if (Win32FileApi.CreateSymbolicLink(Win32FileApi.ToExtendedPath(linkPath), targetPath, flags))
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == Win32FileApi.ErrorPrivilegeNotHeld)
        {
            return false;
        }

        throw ToException(error, linkPath);
    }

    internal static VfsEntry ToEntry(VfsPath path, string parentWindowsPath, NativeEntry native)
    {
        string? target = null;
        if (native.IsReparsePoint)
        {
            var full = Path.Combine(parentWindowsPath, native.Name);
            FileSystemInfo info = native.IsDirectory ? new DirectoryInfo(full) : new FileInfo(full);
            target = info.LinkTarget;
        }

        var kind = KindOf(native.Attributes, native.IsSymlink, native.IsJunction);
        return new VfsEntry(
            path,
            native.Name,
            kind,
            kind == VfsEntryKind.File ? native.Size : null,
            DateTimeOffset.FromFileTime(native.CreatedUtc).ToUniversalTime(),
            DateTimeOffset.FromFileTime(native.ModifiedUtc).ToUniversalTime(),
            native.Attributes,
            target);
    }

    private static VfsEntryKind KindOf(FileAttributes attributes, bool symlink, bool junction)
    {
        if (junction)
        {
            return VfsEntryKind.Junction;
        }

        if (symlink)
        {
            return VfsEntryKind.Symlink;
        }

        return (attributes & FileAttributes.Directory) != 0 ? VfsEntryKind.Directory : VfsEntryKind.File;
    }

    private static bool IsJunction(FileSystemInfo info)
    {
        if ((info.Attributes & FileAttributes.ReparsePoint) == 0 || info is not DirectoryInfo)
        {
            return false;
        }

        var entries = new List<NativeEntry>();
        var parent = Path.GetDirectoryName(info.FullName.TrimEnd('\\'));
        if (parent is null || NativeDirectory.Enumerate(parent, entries) != 0)
        {
            return false;
        }

        return entries.Any(e => e.Name.Equals(info.Name, StringComparison.OrdinalIgnoreCase) && e.IsJunction);
    }

    private static void CopyDirectory(string from, string to, CancellationToken ct)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            ct.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.EnumerateDirectories(from))
        {
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)), ct);
        }
    }

    private static bool WildcardMatch(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t]))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    internal static Exception ToException(int error, string path) => error switch
    {
        Win32FileApi.ErrorAccessDenied => new UnauthorizedAccessException($"Access to '{path}' is denied."),
        Win32FileApi.ErrorPathNotFound or Win32FileApi.ErrorFileNotFound => new DirectoryNotFoundException($"'{path}' was not found."),
        _ => new IOException($"'{path}': Win32 error {error} ({Marshal.GetPInvokeErrorMessage(error)}).", error),
    };
}
