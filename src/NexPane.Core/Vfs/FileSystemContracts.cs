namespace NexPane.Core.Vfs;

[Flags]
public enum VfsCapabilities
{
    None = 0,
    Read = 1,
    Write = 2,
    Delete = 4,
    Rename = 8,
    CreateDirectory = 16,
    Watch = 32,
    SetTimes = 64,
    ServerSideCopy = 128,
    RandomAccessRead = 256,
    AlternateStreams = 512,
    ShellNamespace = 1024,
}

public enum VfsEntryKind
{
    File,
    Directory,
    Symlink,
    Junction,
    ArchiveRoot,
    Virtual,
}

public sealed record VfsEntry(
    VfsPath Path,
    string Name,
    VfsEntryKind Kind,
    long? Size,
    DateTimeOffset? CreatedUtc,
    DateTimeOffset? ModifiedUtc,
    FileAttributes Attributes,
    string? LinkTarget = null,
    ulong? FileId = null)
{
    public bool IsContainer => Kind is VfsEntryKind.Directory or VfsEntryKind.Junction or VfsEntryKind.ArchiveRoot ||
                               (Kind == VfsEntryKind.Symlink && (Attributes & FileAttributes.Directory) != 0);
}

public sealed record EnumerateOptions(bool IncludeHidden = true, bool IncludeSystem = false, string? Pattern = null);

public enum WriteMode
{
    CreateNew,
    Overwrite,
    Append,
}

public sealed record DeleteOptions(bool Recursive = false, bool ToRecycleBin = true);

public interface IFileSystemProvider
{
    string Scheme { get; }

    VfsCapabilities GetCapabilities(VfsPath root);

    IAsyncEnumerable<VfsEntry> EnumerateAsync(VfsPath directory, EnumerateOptions options, CancellationToken ct);

    ValueTask<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken ct);

    ValueTask<Stream> OpenReadAsync(VfsPath path, CancellationToken ct);

    ValueTask<Stream> OpenWriteAsync(VfsPath path, WriteMode mode, CancellationToken ct);

    ValueTask CreateDirectoryAsync(VfsPath path, CancellationToken ct);

    ValueTask DeleteAsync(VfsPath path, DeleteOptions options, CancellationToken ct);

    ValueTask MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken ct);

    ValueTask SetTimesAsync(VfsPath path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct);

    /// <summary>Returns a change watcher, or null when unsupported (the caller polls).</summary>
    IVfsWatcher? CreateWatcher(VfsPath directory, bool recursive);
}

public interface IVfsWatcher : IAsyncDisposable
{
    IAsyncEnumerable<VfsChange> Changes(CancellationToken ct);
}

public sealed record VfsChange(VfsChangeKind Kind, VfsPath Path, VfsPath? OldPath = null);

public enum VfsChangeKind
{
    Created,
    Deleted,
    Modified,
    Renamed,
    Overflow,
}

public interface IFileSystemRouter
{
    IFileSystemProvider Resolve(VfsPath path);

    void Register(IFileSystemProvider provider);
}

/// <summary>Cross-provider copy/move with progress (local↔remote, archive↔local).</summary>
public interface ITransferService
{
    Task<TransferResult> CopyAsync(IReadOnlyList<VfsPath> sources, VfsPath destinationDir,
        TransferOptions options, IProgress<TransferProgress> progress, CancellationToken ct);

    Task<TransferResult> MoveAsync(IReadOnlyList<VfsPath> sources, VfsPath destinationDir,
        TransferOptions options, IProgress<TransferProgress> progress, CancellationToken ct);
}

public sealed record TransferOptions(ConflictPolicy OnConflict, bool PreserveTimes = true, bool VerifySha256 = false);

public enum ConflictPolicy
{
    Ask,
    Overwrite,
    Skip,
    KeepBoth,
    OverwriteIfNewer,
}

public sealed record TransferProgress(long BytesDone, long BytesTotal, int ItemsDone, int ItemsTotal, string? CurrentItem);

public sealed record TransferResult(int Succeeded, int Skipped, IReadOnlyList<(VfsPath Path, string Error)> Failed);

/// <summary>Thread-safe scheme → provider registry.</summary>
public sealed class FileSystemRouter : IFileSystemRouter
{
    private readonly Dictionary<string, IFileSystemProvider> _providers = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public IFileSystemProvider Resolve(VfsPath path)
    {
        lock (_gate)
        {
            return _providers.TryGetValue(path.Scheme, out var provider)
                ? provider
                : throw new NotSupportedException($"No file system provider is registered for scheme '{path.Scheme}:'.");
        }
    }

    public void Register(IFileSystemProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_gate)
        {
            _providers[provider.Scheme.ToLowerInvariant()] = provider;
        }
    }
}
