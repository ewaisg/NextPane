using NexPane.Core.Indexing;

namespace NexPane.Indexing.Store;

/// <summary>
/// Writer-side lookup tables of one segment (§7.1 "FRN map"): file id → index, and (parent, folded name) → index for
/// path-addressed changes. Both are open-addressing tables of <c>int</c> slots whose keys are read back from the segment
/// columns, so they cost ≈ 8 bytes per entry each. Index writer thread only.
/// </summary>
internal sealed class SegmentMaps
{
    private const int Empty = -1;
    private const int Tomb = -2;

    private readonly IndexSegment _segment;
    private int[] _ids = NewTable(16);
    private int _idUsed;
    private int _idTombs;
    private int[] _children = NewTable(16);
    private int _childUsed;
    private int _childTombs;

    public SegmentMaps(IndexSegment segment) => _segment = segment;

    public long AllocatedBytes => ((long)_ids.Length + _children.Length) * sizeof(int);

    // ------------------------------------------------------------ file id map

    public bool TryGetById(ulong fileId, out int index)
    {
        var mask = _ids.Length - 1;
        var slot = (int)(Mix(fileId) & (uint)mask);
        while (true)
        {
            var v = _ids[slot];
            if (v == Empty)
            {
                index = -1;
                return false;
            }

            if (v >= 0 && _segment.GetFileId(v) == fileId && !_segment.IsDeleted(v))
            {
                index = v;
                return true;
            }

            slot = (slot + 1) & mask;
        }
    }

    public void SetId(ulong fileId, int index)
    {
        RemoveId(fileId);
        if ((_idUsed + _idTombs + 1) * 2 > _ids.Length)
        {
            _ids = RehashIds(_ids.Length * (_idUsed * 4 > _ids.Length ? 2 : 1));
        }

        var mask = _ids.Length - 1;
        var slot = (int)(Mix(fileId) & (uint)mask);
        while (_ids[slot] >= 0)
        {
            slot = (slot + 1) & mask;
        }

        if (_ids[slot] == Tomb)
        {
            _idTombs--;
        }

        _ids[slot] = index;
        _idUsed++;
    }

    public void RemoveId(ulong fileId)
    {
        var mask = _ids.Length - 1;
        var slot = (int)(Mix(fileId) & (uint)mask);
        while (true)
        {
            var v = _ids[slot];
            if (v == Empty)
            {
                return;
            }

            if (v >= 0 && _segment.GetFileId(v) == fileId)
            {
                _ids[slot] = Tomb;
                _idUsed--;
                _idTombs++;
                return;
            }

            slot = (slot + 1) & mask;
        }
    }

    // ------------------------------------------------------------ child map

    public bool TryGetChild(int parent, ReadOnlySpan<char> foldedName, out int index)
    {
        var mask = _children.Length - 1;
        var slot = (int)(ChildHash(parent, foldedName) & (uint)mask);
        while (true)
        {
            var v = _children[slot];
            if (v == Empty)
            {
                index = -1;
                return false;
            }

            if (v >= 0 && _segment.GetParent(v) == parent && !_segment.IsDeleted(v) && _segment.GetFoldedName(v).SequenceEqual(foldedName))
            {
                index = v;
                return true;
            }

            slot = (slot + 1) & mask;
        }
    }

    /// <summary>Registers <paramref name="index"/> under its current parent and name.</summary>
    public void AddChild(int index)
    {
        if (index == 0)
        {
            return;
        }

        if ((_childUsed + _childTombs + 1) * 2 > _children.Length)
        {
            _children = RehashChildren(_children.Length * (_childUsed * 4 > _children.Length ? 2 : 1));
        }

        var parent = _segment.GetParent(index);
        var name = _segment.GetFoldedName(index);
        var mask = _children.Length - 1;
        var slot = (int)(ChildHash(parent, name) & (uint)mask);
        while (_children[slot] >= 0)
        {
            slot = (slot + 1) & mask;
        }

        if (_children[slot] == Tomb)
        {
            _childTombs--;
        }

        _children[slot] = index;
        _childUsed++;
    }

    /// <summary>Unregisters <paramref name="index"/>; must be called while its parent and name are still the old ones.</summary>
    public void RemoveChild(int index)
    {
        if (index == 0)
        {
            return;
        }

        var parent = _segment.GetParent(index);
        var name = _segment.GetFoldedName(index);
        var mask = _children.Length - 1;
        var slot = (int)(ChildHash(parent, name) & (uint)mask);
        while (true)
        {
            var v = _children[slot];
            if (v == Empty)
            {
                return;
            }

            if (v == index)
            {
                _children[slot] = Tomb;
                _childUsed--;
                _childTombs++;
                return;
            }

            slot = (slot + 1) & mask;
        }
    }

    /// <summary>Resolves a relative path (components separated by <paramref name="separator"/>) to an entry index.</summary>
    public bool TryResolvePath(ReadOnlySpan<char> relativePath, char separator, out int index)
    {
        index = 0;
        relativePath = relativePath.Trim(separator);
        if (relativePath.IsEmpty)
        {
            return true;
        }

        Span<char> folded = stackalloc char[256];
        foreach (var range in relativePath.Split(separator))
        {
            var part = relativePath[range];
            if (part.IsEmpty)
            {
                continue;
            }

            var buffer = part.Length <= folded.Length ? folded[..part.Length] : new char[part.Length];
            NameArena.Fold(part, buffer);
            if (!TryGetChild(index, buffer, out index))
            {
                return false;
            }
        }

        return true;
    }

    private int[] RehashIds(int size)
    {
        var table = NewTable(size);
        var mask = size - 1;
        var used = 0;
        foreach (var v in _ids)
        {
            if (v < 0)
            {
                continue;
            }

            var slot = (int)(Mix(_segment.GetFileId(v)) & (uint)mask);
            while (table[slot] != Empty)
            {
                slot = (slot + 1) & mask;
            }

            table[slot] = v;
            used++;
        }

        _idUsed = used;
        _idTombs = 0;
        return table;
    }

    private int[] RehashChildren(int size)
    {
        var table = NewTable(size);
        var mask = size - 1;
        var used = 0;
        foreach (var v in _children)
        {
            if (v < 0)
            {
                continue;
            }

            var slot = (int)(ChildHash(_segment.GetParent(v), _segment.GetFoldedName(v)) & (uint)mask);
            while (table[slot] != Empty)
            {
                slot = (slot + 1) & mask;
            }

            table[slot] = v;
            used++;
        }

        _childUsed = used;
        _childTombs = 0;
        return table;
    }

    private static int[] NewTable(int size)
    {
        var table = new int[size];
        Array.Fill(table, Empty);
        return table;
    }

    private static uint Mix(ulong key)
    {
        key ^= key >> 33;
        key *= 0xff51afd7ed558ccdUL;
        key ^= key >> 33;
        key *= 0xc4ceb9fe1a85ec53UL;
        key ^= key >> 33;
        return (uint)key;
    }

    private static uint ChildHash(int parent, ReadOnlySpan<char> foldedName) =>
        (uint)HashCode.Combine(parent, string.GetHashCode(foldedName));
}
