namespace NexPane.Core.Indexing;

/// <summary>Identity of an entry inside one index generation: segment (volume) and index within it.</summary>
public readonly record struct EntryRef(int Segment, int Index);

/// <summary>Per-entry flags (§7.1).</summary>
[Flags]
public enum IndexEntryState : ushort
{
    None = 0,
    Directory = 1,
    Hidden = 2,
    System = 4,
    ReparsePoint = 8,
    Deleted = 16,
    StatPending = 32,
    ContentIndexed = 64,
}

/// <summary>Value copy of one index entry (read through <see cref="IndexSegment.Get"/>).</summary>
public readonly record struct IndexEntry(
    int ParentId,
    IndexEntryState Flags,
    uint Attributes,
    long Size,
    long ModifiedUtc,
    long CreatedUtc,
    ulong FileId)
{
    public bool IsDirectory => (Flags & IndexEntryState.Directory) != 0;

    public bool IsDeleted => (Flags & IndexEntryState.Deleted) != 0;
}

/// <summary>Volume kinds, numerically equal to <c>volumes.kind</c> in index.db (§9.1).</summary>
public enum VolumeKind
{
    NtfsFixed = 0,
    OtherLocal = 1,
    Removable = 2,
    Smb = 3,
    Ftp = 4,
    Ftps = 5,
    Sftp = 6,
    WebDav = 7,
}

/// <summary>Where a volume's entries come from, numerically equal to <c>volumes.source</c> (§9.1).</summary>
public enum VolumeSourceKind
{
    Walker = 0,
    UsnService = 1,
    UsnInProc = 2,
    Crawler = 3,
}

/// <summary>Scan state, numerically equal to <c>volumes.scan_state</c> (§9.1).</summary>
public enum VolumeScanState
{
    Never = 0,
    Scanning = 1,
    Complete = 2,
    Stale = 3,
    Error = 4,
}

/// <summary>Static description of an indexed volume / location.</summary>
/// <param name="RootPath">Display root: <c>C:\</c>, <c>\\nas\media</c>, <c>sftp://user@host:22/home</c>.</param>
/// <param name="Separator">Separator used when building child paths (<c>\</c> for Windows paths, <c>/</c> for remote URIs).</param>
public sealed record IndexVolumeInfo(
    string RootPath,
    VolumeKind Kind,
    char Separator = '\\',
    ulong VolumeSerial = 0,
    string? FileSystem = null,
    string? VolumeGuid = null)
{
    /// <summary>True when file ids are real NTFS file reference numbers (tags then key by FRN, §9.2).</summary>
    public bool HasStableFileIds => Kind == VolumeKind.NtfsFixed;
}

/// <summary>UI-visible status of one indexed volume (P3.17, P4.16).</summary>
public sealed record VolumeStatus(
    string Root,
    VolumeKind Kind,
    VolumeSourceKind Source,
    VolumeScanState State,
    long EntryCount,
    string? Detail = null,
    string? LastError = null);

/// <summary>Kind of change applied to an entry.</summary>
public enum IndexChangeKind : byte
{
    Added,
    Removed,
    Renamed,
    Modified,
}

/// <summary>One applied change, reported by <see cref="IIndexSnapshotSource.DeltaApplied"/>.</summary>
public readonly record struct IndexChange(IndexChangeKind Kind, EntryRef Entry);

/// <summary>Applied changes of one writer batch (§6.3). Raised on the index writer thread; handlers must be quick.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1710:Identifiers should have correct suffix", Justification = "Name fixed by ARCHITECTURE §10.2 (IIndexSnapshotSource.DeltaApplied).")]
public sealed class IndexDeltaBatch(long generation, IReadOnlyList<IndexChange> changes, IReadOnlyList<int>? resetSegments = null) : EventArgs
{
    public long Generation { get; } = generation;

    public IReadOnlyList<IndexChange> Changes { get; } = changes;

    /// <summary>Segments that were replaced wholesale (compaction or reset); entry refs into them must be remapped.</summary>
    public IReadOnlyList<int> ResetSegments { get; } = resetSegments ?? [];
}

/// <summary>Progress notifications of the indexer.</summary>
public sealed record IndexProgress(string Root, VolumeScanState State, long EntriesSeen, string? Message = null);

/// <summary>Options for a user-added index location.</summary>
public sealed record IndexLocationOptions(bool IncludeHidden = true, bool FollowReparsePoints = false, bool IndexContent = false);
