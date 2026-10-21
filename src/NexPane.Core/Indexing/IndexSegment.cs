using System.Runtime.CompilerServices;

namespace NexPane.Core.Indexing;

/// <summary>
/// One volume's entries as a struct-of-arrays (§7.1), grown in 64 Ki-entry chunks (chunks never move; only the chunk
/// table is copied when it grows). Entry 0 is the volume root (its parent is itself).
/// <para>
/// <b>Concurrency:</b> one writer thread (the index writer) calls the mutation methods; any number of readers call the
/// read methods without locks. Every column value is written with a single aligned store, and the name of an entry is
/// one 64-bit <c>(arena offset &lt;&lt; 16 | length)</c> reference, so readers never observe torn values. Appended
/// entries become visible when the writer calls <see cref="Publish"/>; readers bound iteration by the count captured in
/// their <see cref="IndexGeneration"/>. Renames, moves and stat changes update an entry in place (ids stay stable so
/// children keep their parent); deletions set <see cref="IndexEntryState.Deleted"/> (tombstone) until compaction.
/// </para>
/// </summary>
public sealed class IndexSegment
{
    public const int ChunkShift = 16;
    public const int ChunkSize = 1 << ChunkShift;
    private const int ChunkMask = ChunkSize - 1;
    private const int MaxDepth = 4096;

    private readonly NameArena _names = new();
    private readonly NameArena _folded = new();
    private readonly DirectoryPathCache _pathCache;
    private Columns _columns = new(0);
    private int _count;
    private int _allocated;
    private int _tombstones;
    private int _pathEpoch;
    private IndexVolumeInfo _volume;

    public IndexSegment(int id, IndexVolumeInfo volume, int pathCacheCapacity = DirectoryPathCache.DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(volume);
        Id = id;
        _volume = volume;
        _pathCache = new DirectoryPathCache(pathCacheCapacity);
    }

    public int Id { get; }

    public IndexVolumeInfo Volume => Volatile.Read(ref _volume);

    /// <summary>Published entry count (including tombstones).</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Entries appended but not yet published (writer view).</summary>
    public int AllocatedCount => _allocated;

    public int TombstoneCount => Volatile.Read(ref _tombstones);

    public int LiveCount => Count - TombstoneCount;

    /// <summary>Path cache statistics (hits, misses) for diagnostics and tests.</summary>
    public (long Hits, long Misses) PathCacheStats => _pathCache.Stats;

    // ---------------------------------------------------------------- reads (lock-free)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<char> GetName(int index)
    {
        var nameRef = Volatile.Read(ref NameRefSlot(Volatile.Read(ref _columns), index));
        return _names.Read(nameRef >> 16, (int)(nameRef & 0xFFFF));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<char> GetFoldedName(int index)
    {
        var nameRef = Volatile.Read(ref NameRefSlot(Volatile.Read(ref _columns), index));
        return _folded.Read(nameRef >> 16, (int)(nameRef & 0xFFFF));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetNameLength(int index) =>
        (int)(Volatile.Read(ref NameRefSlot(Volatile.Read(ref _columns), index)) & 0xFFFF);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetParent(int index)
    {
        var c = Volatile.Read(ref _columns);
        return Volatile.Read(ref c.Parent[index >> ChunkShift][index & ChunkMask]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IndexEntryState GetFlags(int index)
    {
        var c = Volatile.Read(ref _columns);
        return (IndexEntryState)Volatile.Read(ref c.Flags[index >> ChunkShift][index & ChunkMask]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsDeleted(int index) => (GetFlags(index) & IndexEntryState.Deleted) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsDirectory(int index) => (GetFlags(index) & IndexEntryState.Directory) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint GetAttributes(int index)
    {
        var c = Volatile.Read(ref _columns);
        return Volatile.Read(ref c.Attributes[index >> ChunkShift][index & ChunkMask]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetSize(int index)
    {
        var c = Volatile.Read(ref _columns);
        return Volatile.Read(ref c.Size[index >> ChunkShift][index & ChunkMask]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetModified(int index)
    {
        var c = Volatile.Read(ref _columns);
        return Volatile.Read(ref c.Modified[index >> ChunkShift][index & ChunkMask]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetCreated(int index)
    {
        var c = Volatile.Read(ref _columns);
        return Volatile.Read(ref c.Created[index >> ChunkShift][index & ChunkMask]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong GetFileId(int index)
    {
        var c = Volatile.Read(ref _columns);
        return Volatile.Read(ref c.FileId[index >> ChunkShift][index & ChunkMask]);
    }

    public IndexEntry Get(int index) => new(
        GetParent(index), GetFlags(index), GetAttributes(index), GetSize(index), GetModified(index), GetCreated(index), GetFileId(index));

    /// <summary>Extension of the entry name without the dot (folded), or empty.</summary>
    public ReadOnlySpan<char> GetFoldedExtension(int index)
    {
        var name = GetFoldedName(index);
        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? [] : name[(dot + 1)..];
    }

    /// <summary>Number of ancestors between the entry and the root (root = 0, its children = 1).</summary>
    public int GetDepth(int index)
    {
        var depth = 0;
        var current = index;
        while (current != 0 && depth < MaxDepth)
        {
            current = GetParent(current);
            depth++;
        }

        return depth;
    }

    /// <summary>Full path of an entry, reconstructed through parents with the directory path cache (§7.1, P3.07).</summary>
    public string GetFullPath(int index)
    {
        if (index == 0)
        {
            return Volume.RootPath;
        }

        var parentPath = GetDirectoryPath(GetParent(index));
        return Join(parentPath, GetName(index), Volume.Separator);
    }

    /// <summary>Full path of a directory entry (cached in the LRU).</summary>
    public string GetDirectoryPath(int directoryIndex)
    {
        if (directoryIndex == 0)
        {
            return Volume.RootPath;
        }

        var epoch = Volatile.Read(ref _pathEpoch);
        if (_pathCache.TryGet(directoryIndex, epoch, out var cached))
        {
            return cached;
        }

        // Walk up to the first cached ancestor (or the root), then build down.
        Span<int> stackBuffer = stackalloc int[64];
        var chain = new ValueList(stackBuffer);
        var current = directoryIndex;
        string basePath;
        while (true)
        {
            if (current == 0)
            {
                basePath = Volume.RootPath;
                break;
            }

            if (current != directoryIndex && _pathCache.TryGet(current, epoch, out var ancestor))
            {
                basePath = ancestor;
                break;
            }

            chain.Add(current);
            if (chain.Count > MaxDepth)
            {
                throw new InvalidOperationException($"Parent chain of entry {directoryIndex} in segment {Id} exceeds {MaxDepth} levels (cycle?).");
            }

            current = GetParent(current);
        }

        var separator = Volume.Separator;
        var path = basePath;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var dir = chain[i];
            path = Join(path, GetName(dir), separator);
            _pathCache.Add(dir, path, epoch);
        }

        return path;
    }

    // ---------------------------------------------------------------- writes (index writer thread only)

    /// <summary>Appends an entry; it becomes visible to readers at the next <see cref="Publish"/>. Returns its index.</summary>
    public int Append(ulong fileId, int parent, ReadOnlySpan<char> name, IndexEntryState flags, uint attributes, long size, long modifiedUtc, long createdUtc)
    {
        if (name.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(name), "Name is too long.");
        }

        var index = _allocated;
        if (index == int.MaxValue)
        {
            throw new InvalidOperationException("Segment is full.");
        }

        var c = _columns;
        if ((index >> ChunkShift) >= c.ChunkCount)
        {
            c = c.Grow();
            Volatile.Write(ref _columns, c);
        }

        var chunk = index >> ChunkShift;
        var slot = index & ChunkMask;
        c.EnsureChunk(chunk);
        var offset = _names.Append(name);
        var foldedOffset = _folded.AppendFolded(name);
        if (offset != foldedOffset)
        {
            throw new InvalidOperationException("Name arenas diverged.");
        }

        c.NameRef[chunk][slot] = (offset << 16) | (uint)name.Length;
        c.Parent[chunk][slot] = parent < 0 ? index : parent;
        c.Flags[chunk][slot] = (ushort)flags;
        c.Attributes[chunk][slot] = attributes;
        c.Size[chunk][slot] = size;
        c.Modified[chunk][slot] = modifiedUtc;
        c.Created[chunk][slot] = createdUtc;
        c.FileId[chunk][slot] = fileId;
        _allocated = index + 1;
        if ((flags & IndexEntryState.Deleted) != 0)
        {
            _tombstones++;
        }

        return index;
    }

    /// <summary>Makes appended entries visible to new generations.</summary>
    public void Publish() => Volatile.Write(ref _count, _allocated);

    public void SetName(int index, ReadOnlySpan<char> name)
    {
        var offset = _names.Append(name);
        var foldedOffset = _folded.AppendFolded(name);
        if (offset != foldedOffset)
        {
            throw new InvalidOperationException("Name arenas diverged.");
        }

        var c = _columns;
        Volatile.Write(ref c.NameRef[index >> ChunkShift][index & ChunkMask], (offset << 16) | (uint)name.Length);
        if (IsDirectory(index))
        {
            InvalidatePaths();
        }
    }

    public void SetParent(int index, int parent)
    {
        var c = _columns;
        Volatile.Write(ref c.Parent[index >> ChunkShift][index & ChunkMask], parent);
        if (IsDirectory(index))
        {
            InvalidatePaths();
        }
    }

    public void SetFlags(int index, IndexEntryState flags)
    {
        var c = _columns;
        var old = (IndexEntryState)c.Flags[index >> ChunkShift][index & ChunkMask];
        if ((old & IndexEntryState.Directory) != (flags & IndexEntryState.Directory))
        {
            InvalidatePaths();
        }

        Volatile.Write(ref c.Flags[index >> ChunkShift][index & ChunkMask], (ushort)flags);
    }

    public void SetStat(int index, uint attributes, long size, long modifiedUtc, long createdUtc)
    {
        var c = _columns;
        var chunk = index >> ChunkShift;
        var slot = index & ChunkMask;
        Volatile.Write(ref c.Attributes[chunk][slot], attributes);
        Volatile.Write(ref c.Size[chunk][slot], size);
        Volatile.Write(ref c.Modified[chunk][slot], modifiedUtc);
        Volatile.Write(ref c.Created[chunk][slot], createdUtc);
    }

    /// <summary>Tombstones an entry. Returns false when it already was deleted.</summary>
    public bool MarkDeleted(int index)
    {
        var flags = GetFlags(index);
        if ((flags & IndexEntryState.Deleted) != 0)
        {
            return false;
        }

        var c = _columns;
        Volatile.Write(ref c.Flags[index >> ChunkShift][index & ChunkMask], (ushort)(flags | IndexEntryState.Deleted));
        Interlocked.Increment(ref _tombstones);
        if ((flags & IndexEntryState.Directory) != 0)
        {
            InvalidatePaths();
        }

        return true;
    }

    public void SetVolume(IndexVolumeInfo volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        Volatile.Write(ref _volume, volume);
        InvalidatePaths();
    }

    /// <summary>Invalidates all cached directory paths (after a directory rename/move/delete).</summary>
    public void InvalidatePaths()
    {
        Interlocked.Increment(ref _pathEpoch);
        _pathCache.Clear();
    }

    /// <summary>Bytes held by columns and arenas (excluding writer-side lookup tables).</summary>
    public long EstimateMemoryBytes()
    {
        var c = Volatile.Read(ref _columns);
        const int bytesPerEntry = 8 + 4 + 2 + 4 + 8 + 8 + 8 + 8;
        return ((long)c.AllocatedChunks * ChunkSize * bytesPerEntry) + _names.AllocatedBytes + _folded.AllocatedBytes;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref long NameRefSlot(Columns c, int index) => ref c.NameRef[index >> ChunkShift][index & ChunkMask];

    private static string Join(string parent, ReadOnlySpan<char> name, char separator)
    {
        var needsSeparator = parent.Length > 0 && parent[^1] != separator;
        ReadOnlySpan<char> sep = needsSeparator ? [separator] : [];
        return string.Concat(parent.AsSpan(), sep, name);
    }

    /// <summary>Column chunk tables. Chunks are shared between versions; only the outer tables are copied on growth.</summary>
    private sealed class Columns
    {
        public Columns(int chunkCount)
        {
            ChunkCount = chunkCount;
            NameRef = new long[chunkCount][];
            Parent = new int[chunkCount][];
            Flags = new ushort[chunkCount][];
            Attributes = new uint[chunkCount][];
            Size = new long[chunkCount][];
            Modified = new long[chunkCount][];
            Created = new long[chunkCount][];
            FileId = new ulong[chunkCount][];
        }

        public int ChunkCount { get; }

        public long[][] NameRef { get; }

        public int[][] Parent { get; }

        public ushort[][] Flags { get; }

        public uint[][] Attributes { get; }

        public long[][] Size { get; }

        public long[][] Modified { get; }

        public long[][] Created { get; }

        public ulong[][] FileId { get; }

        public Columns Grow()
        {
            var next = new Columns(Math.Max(1, ChunkCount * 2));
            Array.Copy(NameRef, next.NameRef, ChunkCount);
            Array.Copy(Parent, next.Parent, ChunkCount);
            Array.Copy(Flags, next.Flags, ChunkCount);
            Array.Copy(Attributes, next.Attributes, ChunkCount);
            Array.Copy(Size, next.Size, ChunkCount);
            Array.Copy(Modified, next.Modified, ChunkCount);
            Array.Copy(Created, next.Created, ChunkCount);
            Array.Copy(FileId, next.FileId, ChunkCount);
            next.AllocatedChunks = AllocatedChunks;
            return next;
        }

        /// <summary>Number of chunks whose arrays exist (chunks are allocated lazily, on first append).</summary>
        public int AllocatedChunks { get; private set; }

        public void EnsureChunk(int i)
        {
            if (NameRef[i] is not null)
            {
                return;
            }

            NameRef[i] = new long[ChunkSize];
            Parent[i] = new int[ChunkSize];
            Flags[i] = new ushort[ChunkSize];
            Attributes[i] = new uint[ChunkSize];
            Size[i] = new long[ChunkSize];
            Modified[i] = new long[ChunkSize];
            Created[i] = new long[ChunkSize];
            FileId[i] = new ulong[ChunkSize];
            AllocatedChunks = i + 1;
        }
    }

    /// <summary>Small stack-first list of ints.</summary>
    private ref struct ValueList(Span<int> initial)
    {
        private Span<int> _span = initial;

        public int Count { get; private set; }

        public readonly int this[int i] => _span[i];

        public void Add(int value)
        {
            if (Count == _span.Length)
            {
                var bigger = new int[_span.Length * 2];
                _span.CopyTo(bigger);
                _span = bigger;
            }

            _span[Count++] = value;
        }
    }
}
