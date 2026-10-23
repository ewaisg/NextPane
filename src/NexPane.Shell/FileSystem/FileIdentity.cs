using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NexPane.Shell.FileSystem;

/// <summary>128-bit file identity (<c>FILE_ID_INFO</c>): volume serial + file id (NTFS FRN in the low 64 bits).</summary>
public readonly record struct FileIdentity(ulong VolumeSerial, ulong FileIdLow, ulong FileIdHigh)
{
    /// <summary>Reads the identity of a file or directory without following reparse points.</summary>
    public static unsafe bool TryGet(string path, out FileIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(path);
        identity = default;
        using SafeFileHandle handle = Win32FileApi.CreateFile(Win32FileApi.ToExtendedPath(path), 0, Win32FileApi.FileShareAll, IntPtr.Zero,
            Win32FileApi.OpenExisting, Win32FileApi.FileFlagBackupSemantics | Win32FileApi.FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return false;
        }

        FileIdInfo info;
        if (!GetFileInformationByHandleEx(handle, FileIdInfoClass, &info, (uint)sizeof(FileIdInfo)))
        {
            return false;
        }

        identity = new FileIdentity(info.VolumeSerialNumber, info.FileIdLow, info.FileIdHigh);
        return true;
    }

    private const int FileIdInfoClass = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, void* info, uint size);
}
