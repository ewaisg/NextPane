using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NexPane.Shell.FileSystem;

/// <summary>Raw kernel32 file-system entry points used by the provider, walker and change watchers.</summary>
internal static partial class Win32FileApi
{
    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorAccessDenied = 5;
    public const int ErrorNoMoreFiles = 18;
    public const int ErrorNotSupported = 50;
    public const int ErrorInvalidParameter = 87;
    public const int ErrorInvalidFunction = 1;
    public const int ErrorOperationAborted = 995;
    public const int ErrorIoPending = 997;
    public const int ErrorNotifyEnumDir = 1022;
    public const int ErrorPrivilegeNotHeld = 1314;

    public const uint FileListDirectory = 0x0001;
    public const uint FileShareAll = 0x1 | 0x2 | 0x4;
    public const uint OpenExisting = 3;
    public const uint FileFlagBackupSemantics = 0x02000000;
    public const uint FileFlagOverlapped = 0x40000000;
    public const uint FileFlagOpenReparsePoint = 0x00200000;

    public const int FindExInfoBasic = 1;
    public const int FindExSearchNameMatch = 0;
    public const int FindFirstExLargeFetch = 2;

    public const uint IoReparseTagMountPoint = 0xA0000003;
    public const uint IoReparseTagSymlink = 0xA000000C;

    public const uint SymbolicLinkFlagDirectory = 0x1;
    public const uint SymbolicLinkFlagAllowUnprivilegedCreate = 0x2;

    public const int ReadDirectoryNotifyInformation = 1;
    public const int ReadDirectoryNotifyExtendedInformation = 2;

    [InlineArray(260)]
    public struct FileNameBuffer
    {
        private char _element0;
    }

    [InlineArray(14)]
    public struct AlternateNameBuffer
    {
        private char _element0;
    }

    // FILETIME members are 4-byte aligned in WIN32_FIND_DATAW.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Win32FindData
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Reserved0;
        public uint Reserved1;
        public FileNameBuffer FileName;
        public AlternateNameBuffer AlternateFileName;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static unsafe partial IntPtr FindFirstFileEx(string fileName, int infoLevel, Win32FindData* findData, int searchOp, IntPtr searchFilter, int additionalFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextFileW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool FindNextFile(IntPtr findHandle, Win32FindData* findData);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindClose(IntPtr findHandle);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateSymbolicLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool CreateSymbolicLink(string symlinkFileName, string targetFileName, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "ReadDirectoryChangesW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool ReadDirectoryChanges(SafeFileHandle directory, void* buffer, uint bufferLength, [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        uint notifyFilter, IntPtr bytesReturned, NativeOverlapped* overlapped, IntPtr completionRoutine);

    [LibraryImport("kernel32.dll", EntryPoint = "ReadDirectoryChangesExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool ReadDirectoryChangesEx(SafeFileHandle directory, void* buffer, uint bufferLength, [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        uint notifyFilter, IntPtr bytesReturned, NativeOverlapped* overlapped, IntPtr completionRoutine, int informationClass);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool CancelIoEx(SafeFileHandle file, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetVolumeInformation(string rootPathName, char* volumeNameBuffer, int volumeNameSize, out uint volumeSerialNumber,
        out uint maximumComponentLength, out uint fileSystemFlags, char* fileSystemNameBuffer, int fileSystemNameSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetVolumePathName(string fileName, char* volumePathName, int bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, char* volumeName, int bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetDriveType(string rootPathName);

    /// <summary>Adds the <c>\\?\</c> (or <c>\\?\UNC\</c>) prefix so Win32 APIs accept paths longer than MAX_PATH.</summary>
    public static string ToExtendedPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return path;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + path[2..];
        }

        return @"\\?\" + path;
    }
}
