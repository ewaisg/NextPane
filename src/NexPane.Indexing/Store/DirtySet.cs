using System.Numerics;

namespace NexPane.Indexing.Store;

/// <summary>Growable bitmap of entry indexes changed since the last snapshot capture (1 bit per entry).</summary>
internal sealed class DirtySet
{
    private ulong[] _bits = new ulong[16];
    private int _count;

    public int Count => _count;

    public void Add(int index)
    {
        var word = index >> 6;
        if (word >= _bits.Length)
        {
            Array.Resize(ref _bits, Math.Max(word + 1, _bits.Length * 2));
        }

        var mask = 1UL << (index & 63);
        if ((_bits[word] & mask) == 0)
        {
            _bits[word] |= mask;
            _count++;
        }
    }

    public void Remove(int index)
    {
        var word = index >> 6;
        if (word >= _bits.Length)
        {
            return;
        }

        var mask = 1UL << (index & 63);
        if ((_bits[word] & mask) != 0)
        {
            _bits[word] &= ~mask;
            _count--;
        }
    }

    public void Clear()
    {
        Array.Clear(_bits);
        _count = 0;
    }

    public IEnumerable<int> Enumerate()
    {
        for (var w = 0; w < _bits.Length; w++)
        {
            var word = _bits[w];
            while (word != 0)
            {
                var bit = BitOperations.TrailingZeroCount(word);
                yield return (w << 6) + bit;
                word &= word - 1;
            }
        }
    }

    /// <summary>Returns a new set with indexes translated through <paramref name="map"/> (−1 drops the index).</summary>
    public DirtySet Remap(int[] map)
    {
        var result = new DirtySet();
        foreach (var index in Enumerate())
        {
            if (index < map.Length && map[index] >= 0)
            {
                result.Add(map[index]);
            }
        }

        return result;
    }
}
