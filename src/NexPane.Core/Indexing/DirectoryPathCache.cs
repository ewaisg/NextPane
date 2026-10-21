namespace NexPane.Core.Indexing;

/// <summary>
/// Thread-safe LRU of reconstructed directory paths keyed by entry index (§7.1, default 50 000 paths).
/// Entries carry the path epoch of the segment when they were computed; an entry from an older epoch is a miss,
/// which makes invalidation race-free against concurrent readers.
/// </summary>
public sealed class DirectoryPathCache
{
    public const int DefaultCapacity = 50_000;

    private readonly int _capacity;
    private readonly Dictionary<int, LinkedListNode<Item>> _map;
    private readonly LinkedList<Item> _lru = new();
    private readonly Lock _gate = new();
    private long _hits;
    private long _misses;

    public DirectoryPathCache(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _map = new Dictionary<int, LinkedListNode<Item>>(Math.Min(capacity, 4096));
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _map.Count;
            }
        }
    }

    public (long Hits, long Misses) Stats => (Interlocked.Read(ref _hits), Interlocked.Read(ref _misses));

    public bool TryGet(int directoryIndex, int epoch, out string path)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(directoryIndex, out var node) && node.Value.Epoch == epoch)
            {
                if (node != _lru.First)
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                }

                _hits++;
                path = node.Value.Path;
                return true;
            }

            _misses++;
        }

        path = string.Empty;
        return false;
    }

    public void Add(int directoryIndex, string path, int epoch)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(directoryIndex, out var existing))
            {
                existing.Value = new Item(directoryIndex, path, epoch);
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }

            if (_map.Count >= _capacity)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
            }

            _map[directoryIndex] = _lru.AddFirst(new Item(directoryIndex, path, epoch));
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _lru.Clear();
        }
    }

    private readonly record struct Item(int Key, string Path, int Epoch);
}
