using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NexPane.Shell.FileSystem;

/// <summary><c>USN_JOURNAL_DATA_V1</c>.</summary>
public readonly record struct UsnJournalInfo(ulong JournalId, long FirstUsn, long NextUsn, long LowestValidUsn, long MaxUsn, ulong MaximumSize, ulong AllocationDelta);

/// <summary><c>NTFS_VOLUME_DATA_BUFFER</c> (the fields the MFT reader needs).</summary>
public readonly record struct NtfsVolumeData(
    long VolumeSerialNumber,
    long TotalClusters,
    int BytesPerSector,
    int BytesPerCluster,
    int BytesPerFileRecordSegment,
    long MftValidDataLength,
    long MftStartLcn);

/// <summary>Result of an IOCTL that fills a buffer.</summary>
public readonly record struct IoctlResult(int BytesReturned, int Error)
{
    public bool Succeeded => Error == 0;
}

/// <summary>
/// Read-only NTFS volume handle (<c>\\.\C:</c>) for USN journal and raw MFT access (§7.3, P3.10/P3.11). Opening it
/// requires administrator rights; the app uses it in-process only when elevated, otherwise through the Index Service.
/// </summary>
public sealed class NtfsVolume : IDisposable
{
    public const int ErrorHandleEof = 38;
    public const int ErrorJournalDeleteInProgress = 1178;
    public const int ErrorJournalNotActive = 1179;
    public const int ErrorJournalEntryDeleted = 1181;

    private const uint GenericRead = 0x80000000;
    private const uint FsctlQueryUsnJournal = 0x000900F4;
    private const uint FsctlCreateUsnJournal = 0x000900E7;
    private const uint FsctlEnumUsnData = 0x000900B3;
    private const uint FsctlReadUsnJournal = 0x000900BB;
    private const uint FsctlGetNtfsVolumeData = 0x00090064;

    private readonly SafeFileHandle _handle;

    private NtfsVolume(SafeFileHandle handle, string root)
    {
        _handle = handle;
        Root = root;
    }

    /// <summary>Drive root such as <c>C:\</c>.</summary>
    public string Root { get; }

    public SafeFileHandle Handle => _handle;

    /// <summary>Opens <paramref name="driveRoot"/> (e.g. <c>C:\</c> or a volume GUID path) for reading.</summary>
    /// <exception cref="UnauthorizedAccessException">The process is not elevated.</exception>
    public static NtfsVolume Open(string driveRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(driveRoot);
        var device = driveRoot.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase)
            ? driveRoot.TrimEnd('\\')
            : @"\\.\" + driveRoot.TrimEnd('\\');
        var handle = Win32FileApi.CreateFile(device, GenericRead, Win32FileApi.FileShareAll, IntPtr.Zero, Win32FileApi.OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw LocalFileSystemProvider.ToException(error, device);
        }

        return new NtfsVolume(handle, driveRoot.EndsWith('\\') ? driveRoot : driveRoot + "\\");
    }

    /// <summary>Queries the change journal; <paramref name="error"/> is <see cref="ErrorJournalNotActive"/> when none exists.</summary>
    public unsafe bool TryQueryJournal(out UsnJournalInfo info, out int error)
    {
        Span<byte> output = stackalloc byte[80];
        var result = Ioctl(FsctlQueryUsnJournal, [], output);
        error = result.Error;
        info = default;
        if (!result.Succeeded)
        {
            return false;
        }

        info = new UsnJournalInfo(
            BinaryPrimitives.ReadUInt64LittleEndian(output),
            BinaryPrimitives.ReadInt64LittleEndian(output[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(output[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(output[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(output[32..]),
            BinaryPrimitives.ReadUInt64LittleEndian(output[40..]),
            BinaryPrimitives.ReadUInt64LittleEndian(output[48..]));
        return true;
    }

    /// <summary>Creates (or resizes) the change journal (§7.3: 32 MB max, 8 MB delta).</summary>
    public void CreateJournal(ulong maximumSize = 32UL * 1024 * 1024, ulong allocationDelta = 8UL * 1024 * 1024)
    {
        Span<byte> input = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(input, maximumSize);
        BinaryPrimitives.WriteUInt64LittleEndian(input[8..], allocationDelta);
        var result = Ioctl(FsctlCreateUsnJournal, input, []);
        if (!result.Succeeded)
        {
            throw new IOException($"FSCTL_CREATE_USN_JOURNAL on {Root} failed with Win32 error {result.Error}.", result.Error);
        }
    }

    /// <summary><c>FSCTL_ENUM_USN_DATA</c> from <paramref name="startFileReference"/>; output starts with the next FRN.
    /// Error <see cref="ErrorHandleEof"/> marks the end of the MFT.</summary>
    public IoctlResult EnumUsnData(ulong startFileReference, long highUsn, Span<byte> output)
    {
        Span<byte> input = stackalloc byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(input, startFileReference);
        BinaryPrimitives.WriteInt64LittleEndian(input[8..], 0);
        BinaryPrimitives.WriteInt64LittleEndian(input[16..], highUsn);
        BinaryPrimitives.WriteUInt16LittleEndian(input[24..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(input[26..], 3);
        return Ioctl(FsctlEnumUsnData, input[..28], output);
    }

    /// <summary><c>FSCTL_READ_USN_JOURNAL</c> without waiting; output starts with the next USN.</summary>
    public IoctlResult ReadJournal(long startUsn, ulong journalId, Span<byte> output, uint reasonMask = 0xFFFFFFFF)
    {
        Span<byte> input = stackalloc byte[48];
        BinaryPrimitives.WriteInt64LittleEndian(input, startUsn);
        BinaryPrimitives.WriteUInt32LittleEndian(input[8..], reasonMask);
        BinaryPrimitives.WriteUInt32LittleEndian(input[12..], 0); // ReturnOnlyOnClose = false
        BinaryPrimitives.WriteUInt64LittleEndian(input[16..], 0); // Timeout
        BinaryPrimitives.WriteUInt64LittleEndian(input[24..], 0); // BytesToWaitFor = 0: return immediately (polled)
        BinaryPrimitives.WriteUInt64LittleEndian(input[32..], journalId);
        BinaryPrimitives.WriteUInt16LittleEndian(input[40..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(input[42..], 3);
        return Ioctl(FsctlReadUsnJournal, input[..44], output);
    }

    public NtfsVolumeData GetVolumeData()
    {
        Span<byte> output = stackalloc byte[128];
        var result = Ioctl(FsctlGetNtfsVolumeData, [], output);
        if (!result.Succeeded)
        {
            throw new IOException($"FSCTL_GET_NTFS_VOLUME_DATA on {Root} failed with Win32 error {result.Error}.", result.Error);
        }

        return new NtfsVolumeData(
            BinaryPrimitives.ReadInt64LittleEndian(output),
            BinaryPrimitives.ReadInt64LittleEndian(output[16..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(output[40..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(output[44..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(output[48..]),
            BinaryPrimitives.ReadInt64LittleEndian(output[56..]),
            BinaryPrimitives.ReadInt64LittleEndian(output[64..]));
    }

    /// <summary>Raw read at a byte offset of the volume (offset and length must be sector aligned).</summary>
    public int ReadAt(long offset, Span<byte> buffer) => RandomAccess.Read(_handle, buffer, offset);

    public void Dispose() => _handle.Dispose();

    private unsafe IoctlResult Ioctl(uint code, ReadOnlySpan<byte> input, Span<byte> output)
    {
        fixed (byte* inPtr = input)
        fixed (byte* outPtr = output)
        {
            if (DeviceIoControl(_handle, code, inPtr, (uint)input.Length, outPtr, (uint)output.Length, out var returned, IntPtr.Zero))
            {
                return new IoctlResult((int)returned, 0);
            }

            return new IoctlResult(0, Marshal.GetLastPInvokeError());
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, void* inBuffer, uint inBufferSize,
        void* outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);
}

/// <summary>Size and times of a file opened by its file reference number on a volume (<c>OpenFileById</c>).</summary>
public static class FileById
{
    private const int FileBasicInfo = 0;
    private const int FileStandardInfo = 1;

    /// <summary>Reads (attributes, size, created, modified) for <paramref name="fileReference"/>; false when the file
    /// no longer exists or is inaccessible.</summary>
    public static unsafe bool TryStat(SafeFileHandle anyHandleOnVolume, ulong fileReference, out uint attributes, out long size, out long createdUtc, out long modifiedUtc)
    {
        ArgumentNullException.ThrowIfNull(anyHandleOnVolume);
        attributes = 0;
        size = 0;
        createdUtc = 0;
        modifiedUtc = 0;
        var descriptor = new FileIdDescriptor { Size = (uint)sizeof(FileIdDescriptor), Type = 0, FileId = (long)fileReference };
        using var file = OpenFileById(anyHandleOnVolume, ref descriptor, 0x80 /* FILE_READ_ATTRIBUTES */, Win32FileApi.FileShareAll, IntPtr.Zero,
            Win32FileApi.FileFlagBackupSemantics | Win32FileApi.FileFlagOpenReparsePoint);
        if (file.IsInvalid)
        {
            return false;
        }

        BasicInfo basic;
        StandardInfo standard;
        if (!GetFileInformationByHandleEx(file, FileBasicInfo, &basic, (uint)sizeof(BasicInfo)) ||
            !GetFileInformationByHandleEx(file, FileStandardInfo, &standard, (uint)sizeof(StandardInfo)))
        {
            return false;
        }

        attributes = basic.FileAttributes;
        size = standard.Directory != 0 ? 0 : standard.EndOfFile;
        createdUtc = basic.CreationTime;
        modifiedUtc = basic.LastWriteTime;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdDescriptor
    {
        public uint Size;
        public int Type;
        public long FileId;
        public long Padding; // union with FILE_ID_128 (16 bytes)
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(SafeFileHandle volumeHint, ref FileIdDescriptor fileId, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint flagsAndAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, void* info, uint size);
}
