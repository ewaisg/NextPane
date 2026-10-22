using System.Runtime.InteropServices;

namespace NexPane.Shell.FileSystem;

/// <summary>One directory entry from <c>FindFirstFileExW</c>. Times are FILETIME UTC ticks.</summary>
public readonly record struct NativeEntry(string Name, FileAttributes Attributes, long Size, long CreatedUtc, long ModifiedUtc, uint ReparseTag)
{
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;

    public bool IsSymlink => IsReparsePoint && ReparseTag == Win32FileApi.IoReparseTagSymlink;

    public bool IsJunction => IsReparsePoint && ReparseTag == Win32FileApi.IoReparseTagMountPoint;
}

/// <summary>Fast directory listing: <c>FindFirstFileExW(FindExInfoBasic, FIND_FIRST_EX_LARGE_FETCH)</c> over
/// <c>\\?\</c> paths (§7.3), so paths longer than MAX_PATH work and no short names are generated.</summary>
public static class NativeDirectory
{
    /// <summary>Lists <paramref name="directoryPath"/> into <paramref name="output"/>. Returns 0 or the Win32 error code
    /// (e.g. 5 access denied, 3 path not found); <c>.</c> and <c>..</c> are skipped.</summary>
    public static unsafe int Enumerate(string directoryPath, List<NativeEntry> output)
    {
        ArgumentNullException.ThrowIfNull(directoryPath);
        ArgumentNullException.ThrowIfNull(output);
        var pattern = Win32FileApi.ToExtendedPath(directoryPath.TrimEnd('\\') + @"\*");
        Win32FileApi.Win32FindData data;
        var handle = Win32FileApi.FindFirstFileEx(pattern, Win32FileApi.FindExInfoBasic, &data, Win32FileApi.FindExSearchNameMatch, IntPtr.Zero, Win32FileApi.FindFirstExLargeFetch);
        if (handle == new IntPtr(-1))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == Win32FileApi.ErrorFileNotFound ? 0 : error;
        }

        try
        {
            do
            {
                var name = NameOf(ref data);
                if (name is "." or "..")
                {
                    continue;
                }

                var attributes = (FileAttributes)data.FileAttributes;
                var reparseTag = (attributes & FileAttributes.ReparsePoint) != 0 ? data.Reserved0 : 0u;
                output.Add(new NativeEntry(name, attributes, ((long)data.FileSizeHigh << 32) | data.FileSizeLow, data.CreationTime, data.LastWriteTime, reparseTag));
            }
            while (Win32FileApi.FindNextFile(handle, &data));

            var last = Marshal.GetLastPInvokeError();
            return last == Win32FileApi.ErrorNoMoreFiles ? 0 : last;
        }
        finally
        {
            Win32FileApi.FindClose(handle);
        }
    }

    private static string NameOf(ref Win32FileApi.Win32FindData data)
    {
        ReadOnlySpan<char> buffer = data.FileName;
        var end = buffer.IndexOf('\0');
        return new string(end < 0 ? buffer : buffer[..end]);
    }
}

/// <summary>Volume facts used to choose an index source (§7.3).</summary>
public sealed record VolumeFacts(string RootPath, string? VolumeGuidPath, uint Serial, string FileSystem, DriveType DriveType, bool SupportsUsnJournal)
{
    public bool IsNtfs => FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);

    public bool IsNetwork => DriveType == DriveType.Network || RootPath.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>Reads volume facts for the volume containing <paramref name="path"/>.</summary>
    public static unsafe VolumeFacts? TryGet(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var rootBuffer = stackalloc char[1024];
        if (!Win32FileApi.GetVolumePathName(path, rootBuffer, 1024))
        {
            return null;
        }

        var root = new string(rootBuffer);
        var fsBuffer = stackalloc char[64];
        if (!Win32FileApi.GetVolumeInformation(root, null, 0, out var serial, out _, out var flags, fsBuffer, 64))
        {
            return null;
        }

        string? guidPath = null;
        var guidBuffer = stackalloc char[64];
        if (!root.StartsWith(@"\\", StringComparison.Ordinal) && Win32FileApi.GetVolumeNameForVolumeMountPoint(root, guidBuffer, 64))
        {
            guidPath = new string(guidBuffer);
        }

        const uint fileSupportsUsnJournal = 0x02000000;
        var driveType = (DriveType)Win32FileApi.GetDriveType(root);
        return new VolumeFacts(root, guidPath, serial, new string(fsBuffer), driveType, (flags & fileSupportsUsnJournal) != 0);
    }
}
