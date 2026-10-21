namespace NexPane.Core.Indexing;

/// <summary>Kind of a change produced by a volume source (§6.3).</summary>
public enum IndexDeltaKind : byte
{
    /// <summary>Add a new entry or update an existing one (name, parent, attributes, size, times).</summary>
    Upsert,

    /// <summary>Remove an entry; removing a directory removes its whole subtree.</summary>
    Remove,

    /// <summary>Rename and/or move an entry (stat unchanged).</summary>
    Move,

    /// <summary>Remove every entry below <see cref="IndexDelta.Path"/> that is not in <see cref="IndexDelta.Found"/>
    /// (used after a subtree re-walk, e.g. on watcher overflow).</summary>
    Reconcile,
}

/// <summary>
/// One change from a volume source. Entries are identified either by file id (<see cref="FileId"/> ≠ 0: NTFS FRN or a
/// source-assigned synthetic id) or, when <see cref="FileId"/> is 0, by <see cref="Path"/> relative to the volume root
/// (components separated by the volume separator, no leading separator). Times are FILETIME UTC ticks.
/// </summary>
public readonly record struct IndexDelta(
    IndexDeltaKind Kind,
    ulong FileId,
    ulong ParentFileId,
    string? Name,
    uint Attributes,
    long Size,
    long ModifiedUtc,
    long CreatedUtc,
    string? Path = null,
    string? NewPath = null,
    IReadOnlySet<string>? Found = null)
{
    public const uint DirectoryAttribute = 0x10;

    public bool IsDirectory => (Attributes & DirectoryAttribute) != 0;

    public static IndexDelta UpsertById(ulong fileId, ulong parentFileId, string name, uint attributes, long size, long modifiedUtc, long createdUtc) =>
        new(IndexDeltaKind.Upsert, fileId, parentFileId, name, attributes, size, modifiedUtc, createdUtc);

    public static IndexDelta UpsertByPath(string relativePath, uint attributes, long size, long modifiedUtc, long createdUtc) =>
        new(IndexDeltaKind.Upsert, 0, 0, null, attributes, size, modifiedUtc, createdUtc, relativePath);

    public static IndexDelta RemoveById(ulong fileId) => new(IndexDeltaKind.Remove, fileId, 0, null, 0, 0, 0, 0);

    public static IndexDelta RemoveByPath(string relativePath) => new(IndexDeltaKind.Remove, 0, 0, null, 0, 0, 0, 0, relativePath);

    public static IndexDelta MoveById(ulong fileId, ulong newParentFileId, string newName) =>
        new(IndexDeltaKind.Move, fileId, newParentFileId, newName, 0, 0, 0, 0);

    public static IndexDelta MoveByPath(string oldRelativePath, string newRelativePath) =>
        new(IndexDeltaKind.Move, 0, 0, null, 0, 0, 0, 0, oldRelativePath, newRelativePath);

    public static IndexDelta ReconcileSubtree(string relativePath, IReadOnlySet<string> foundRelativePaths) =>
        new(IndexDeltaKind.Reconcile, 0, 0, null, 0, 0, 0, 0, relativePath, null, foundRelativePaths);
}
