using System.Numerics;

namespace NexPane.Core.Indexing;

/// <summary>
/// Lock-free cache of reconstructed directory paths keyed by entry index (§7.1, default 50 000 paths): a 2-way
/// set-associative table with per-set least-recently-used replacement (an approximation of a global LRU that needs no
/// lock on the read path). Entries carry the segment's path epoch; an entry from an older epoch is a miss, which makes
/// invalidation race-free against concurrent readers.
/// </summary>
public sealed class DirectoryPathCache
{
    public const int DefaultCapacity = 50_000;

    private readonly Item?[] _slots;
    private readonly int _setMask;
    private long _hits;
    private long _misses;
    private long _clock;

    public DirectoryPathCache(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        var sets = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, (capacity + 1) / 2));
        _slots = new Item?[sets * 2];
        _setMask = sets - 1;
    }

    /// <summary>Number of slots (≥ the requested capacity).</summary>
    public int Capacity => _slots.Length;

    public int Count => _slots.Count(s => s is not null);

    public (long Hits, long Misses) Stats => (Volatile.Read(ref _hits), Volatile.Read(ref _misses));

    public bool TryGet(int directoryIndex, int epoch, out string path)
    {
        var set = Set(directoryIndex);
        for (var way = 0; way < 2; way++)
        {
            var item = Volatile.Read(ref _slots[set + way]);
            if (item is not null && item.Key == directoryIndex && item.Epoch == epoch)
            {
                item.LastUse = ++_clock; // approximate under races; only orders replacement
                _hits++; // diagnostics only: unsynchronized to keep the hot path free of shared atomics
                path = item.Path;
                return true;
            }
        }

        _misses++;
        path = string.Empty;
        return false;
    }

    public void Add(int directoryIndex, string path, int epoch)
    {
        var set = Set(directoryIndex);
        var a = Volatile.Read(ref _slots[set]);
        var b = Volatile.Read(ref _slots[set + 1]);
        // Replace the same key, else an empty way, else the least recently used way of the set.
        var way = a is not null && a.Key == directoryIndex ? 0
            : b is not null && b.Key == directoryIndex ? 1
            : a is null ? 0
            : b is null ? 1
            : a.LastUse <= b.LastUse ? 0 : 1;
        Volatile.Write(ref _slots[set + way], new Item(directoryIndex, path, epoch) { LastUse = ++_clock });
    }

    public void Clear() => Array.Clear(_slots);

    private int Set(int key) => (int)((uint)(key * -1640531535) & (uint)_setMask) * 2; // Fibonacci hashing spreads sequential ids

    private sealed class Item(int key, string path, int epoch)
    {
        public int Key { get; } = key;

        public string Path { get; } = path;

        public int Epoch { get; } = epoch;

        public long LastUse { get; set; }
    }
}
