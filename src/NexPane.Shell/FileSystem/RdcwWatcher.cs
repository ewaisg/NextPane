using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using NexPane.Core.Vfs;

namespace NexPane.Shell.FileSystem;

public enum RdcwAction
{
    Added = 1,
    Removed = 2,
    Modified = 3,
    RenamedOldName = 4,
    RenamedNewName = 5,

    /// <summary>The kernel buffer overflowed (<c>ERROR_NOTIFY_ENUM_DIR</c> or a zero-byte completion): the watched
    /// subtree must be re-walked (§7.4).</summary>
    Overflow = 100,
}

/// <summary>One change. With <c>ReadDirectoryChangesExW</c> extended information the file id, parent id, attributes,
/// size and times are filled; otherwise only <see cref="Action"/> and <see cref="RelativePath"/> are meaningful.</summary>
public readonly record struct RdcwEvent(
    RdcwAction Action,
    string RelativePath,
    bool Extended = false,
    long FileId = 0,
    long ParentFileId = 0,
    FileAttributes Attributes = 0,
    long Size = 0,
    long CreatedUtc = 0,
    long ModifiedUtc = 0);

/// <summary>
/// Raw <c>ReadDirectoryChangesExW</c> watcher with overlapped I/O on a <see cref="ThreadPoolBoundHandle"/> (§7.4):
/// 64 KB buffer by default (the SMB maximum), filters name/dir-name/attributes/size/last-write/creation, extended
/// information on Windows 10 1709+ with automatic fallback to <c>ReadDirectoryChangesW</c> (e.g. on SMB shares).
/// Events are delivered through <see cref="Events"/>; overflows are reported as <see cref="RdcwAction.Overflow"/>.
/// </summary>
public sealed unsafe class RdcwWatcher : IDisposable
{
    public const int DefaultBufferSize = 64 * 1024;
    private const uint NotifyFilter = 0x1 | 0x2 | 0x4 | 0x8 | 0x10 | 0x40;

    private readonly Channel<RdcwEvent> _events = Channel.CreateUnbounded<RdcwEvent>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly SafeFileHandle _handle;
    private readonly ThreadPoolBoundHandle _bound;
    private readonly byte* _buffer;
    private readonly int _bufferSize;
    private readonly bool _recursive;
    private readonly Lock _gate = new();
    private NativeOverlapped* _pending;
    private bool _extended;
    private bool _disposed;

    public RdcwWatcher(string directory, bool recursive, int bufferSize = DefaultBufferSize, bool preferExtended = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1024);
        Directory = directory;
        _recursive = recursive;
        _bufferSize = bufferSize;
        _extended = preferExtended;
        _handle = Win32FileApi.CreateFile(Win32FileApi.ToExtendedPath(directory), Win32FileApi.FileListDirectory, Win32FileApi.FileShareAll, IntPtr.Zero,
            Win32FileApi.OpenExisting, Win32FileApi.FileFlagBackupSemantics | Win32FileApi.FileFlagOverlapped, IntPtr.Zero);
        if (_handle.IsInvalid)
        {
            throw LocalFileSystemProvider.ToException(Marshal.GetLastPInvokeError(), directory);
        }

        _bound = ThreadPoolBoundHandle.BindHandle(_handle);
        _buffer = (byte*)NativeMemory.AlignedAlloc((nuint)bufferSize, 8);
    }

    public string Directory { get; }

    /// <summary>True while extended (file id + stat) notifications are in use.</summary>
    public bool UsesExtendedInformation => _extended;

    public ChannelReader<RdcwEvent> Events => _events.Reader;

    /// <summary>Number of overflows observed (diagnostics).</summary>
    public int Overflows { get; private set; }

    public void Start()
    {
        lock (_gate)
        {
            Issue();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_pending != null)
            {
                Win32FileApi.CancelIoEx(_handle, _pending);
            }
        }

        // The completion callback (ERROR_OPERATION_ABORTED) frees the overlapped and buffer when a read was pending.
        if (_pending == null)
        {
            Release();
        }

        _events.Writer.TryComplete();
    }

    private void Issue()
    {
        if (_disposed)
        {
            return;
        }

        var overlapped = _bound.AllocateNativeOverlapped(OnCompleted, this, null);
        var ok = _extended
            ? Win32FileApi.ReadDirectoryChangesEx(_handle, _buffer, (uint)_bufferSize, _recursive, NotifyFilter, IntPtr.Zero, overlapped, IntPtr.Zero,
                Win32FileApi.ReadDirectoryNotifyExtendedInformation)
            : Win32FileApi.ReadDirectoryChanges(_handle, _buffer, (uint)_bufferSize, _recursive, NotifyFilter, IntPtr.Zero, overlapped, IntPtr.Zero);
        if (ok)
        {
            _pending = overlapped;
            return;
        }

        var error = Marshal.GetLastPInvokeError();
        _bound.FreeNativeOverlapped(overlapped);
        if (_extended && error is Win32FileApi.ErrorInvalidParameter or Win32FileApi.ErrorInvalidFunction or Win32FileApi.ErrorNotSupported)
        {
            _extended = false; // e.g. SMB shares or older systems
            Issue();
            return;
        }

        _events.Writer.TryComplete(LocalFileSystemProvider.ToException(error, Directory));
    }

    private static void OnCompleted(uint errorCode, uint numBytes, NativeOverlapped* overlapped)
    {
        var watcher = (RdcwWatcher)ThreadPoolBoundHandle.GetNativeOverlappedState(overlapped)!;
        watcher.Complete(errorCode, numBytes, overlapped);
    }

    private void Complete(uint errorCode, uint numBytes, NativeOverlapped* overlapped)
    {
        lock (_gate)
        {
            _pending = null;
            _bound.FreeNativeOverlapped(overlapped);
            if (_disposed || errorCode == Win32FileApi.ErrorOperationAborted)
            {
                Release();
                return;
            }

            if (errorCode == Win32FileApi.ErrorNotifyEnumDir || (errorCode == 0 && numBytes == 0))
            {
                Overflows++;
                _events.Writer.TryWrite(new RdcwEvent(RdcwAction.Overflow, string.Empty));
            }
            else if (errorCode != 0)
            {
                _events.Writer.TryComplete(LocalFileSystemProvider.ToException((int)errorCode, Directory));
                Release();
                return;
            }
            else
            {
                Parse(new ReadOnlySpan<byte>(_buffer, (int)Math.Min(numBytes, (uint)_bufferSize)));
            }

            Issue();
        }
    }

    private void Parse(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            var record = data[offset..];
            var next = BinaryPrimitives.ReadInt32LittleEndian(record);
            var action = (RdcwAction)BinaryPrimitives.ReadInt32LittleEndian(record[4..]);
            if (_extended)
            {
                var nameLength = BinaryPrimitives.ReadInt32LittleEndian(record[80..]);
                var name = MemoryMarshal.Cast<byte, char>(record.Slice(84, nameLength)).ToString();
                _events.Writer.TryWrite(new RdcwEvent(
                    action,
                    name,
                    Extended: true,
                    FileId: BinaryPrimitives.ReadInt64LittleEndian(record[64..]),
                    ParentFileId: BinaryPrimitives.ReadInt64LittleEndian(record[72..]),
                    Attributes: (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(record[56..]),
                    Size: BinaryPrimitives.ReadInt64LittleEndian(record[48..]),
                    CreatedUtc: BinaryPrimitives.ReadInt64LittleEndian(record[8..]),
                    ModifiedUtc: BinaryPrimitives.ReadInt64LittleEndian(record[16..])));
            }
            else
            {
                var nameLength = BinaryPrimitives.ReadInt32LittleEndian(record[8..]);
                var name = MemoryMarshal.Cast<byte, char>(record.Slice(12, nameLength)).ToString();
                _events.Writer.TryWrite(new RdcwEvent(action, name));
            }

            if (next <= 0)
            {
                break;
            }

            offset += next;
        }
    }

    private void Release()
    {
        if (_handle.IsClosed)
        {
            return;
        }

        _bound.Dispose();
        _handle.Dispose();
        NativeMemory.AlignedFree(_buffer);
    }
}

/// <summary><see cref="IVfsWatcher"/> over <see cref="RdcwWatcher"/> for open folders (§7.4).</summary>
public sealed class RdcwVfsWatcher(VfsPath directory, bool recursive) : IVfsWatcher
{
    private readonly RdcwWatcher _watcher = new(directory.ToWindowsPath(), recursive);
    private int _started;

    public async IAsyncEnumerable<VfsChange> Changes([EnumeratorCancellation] CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _watcher.Start();
        }

        string? pendingOldName = null;
        await foreach (var e in _watcher.Events.ReadAllAsync(ct).ConfigureAwait(false))
        {
            switch (e.Action)
            {
                case RdcwAction.Added:
                    yield return new VfsChange(VfsChangeKind.Created, directory.Combine(e.RelativePath));
                    break;
                case RdcwAction.Removed:
                    yield return new VfsChange(VfsChangeKind.Deleted, directory.Combine(e.RelativePath));
                    break;
                case RdcwAction.Modified:
                    yield return new VfsChange(VfsChangeKind.Modified, directory.Combine(e.RelativePath));
                    break;
                case RdcwAction.RenamedOldName:
                    pendingOldName = e.RelativePath;
                    break;
                case RdcwAction.RenamedNewName:
                    yield return new VfsChange(VfsChangeKind.Renamed, directory.Combine(e.RelativePath),
                        pendingOldName is null ? null : directory.Combine(pendingOldName));
                    pendingOldName = null;
                    break;
                case RdcwAction.Overflow:
                    yield return new VfsChange(VfsChangeKind.Overflow, directory);
                    break;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _watcher.Dispose();
        return ValueTask.CompletedTask;
    }
}
