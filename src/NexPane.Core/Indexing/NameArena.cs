namespace NexPane.Core.Indexing;

/// <summary>
/// Append-only UTF-16 arena split into fixed 1 Mi-char blocks (a name never spans two blocks). Blocks never move, so a
/// reader holding an offset can always read it; the block table is replaced (copy + publish) when it grows.
/// Single writer, lock-free readers.
/// </summary>
public sealed class NameArena
{
    public const int BlockShift = 20;
    public const int BlockSize = 1 << BlockShift;
    private const int BlockMask = BlockSize - 1;

    private char[][] _blocks = [new char[BlockSize]];
    private long _next;

    /// <summary>Total characters reserved (including block-end slack).</summary>
    public long Length => Volatile.Read(ref _next);

    /// <summary>Bytes allocated by the arena blocks.</summary>
    public long AllocatedBytes => (long)Volatile.Read(ref _blocks).Length * BlockSize * sizeof(char);

    /// <summary>Appends <paramref name="value"/> (writer thread only) and returns its global offset.</summary>
    public long Append(ReadOnlySpan<char> value)
    {
        if (value.Length > BlockSize)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Name is longer than an arena block.");
        }

        var offset = _next;
        var within = (int)(offset & BlockMask);
        if (within + value.Length > BlockSize)
        {
            offset = ((offset >> BlockShift) + 1) << BlockShift;
            within = 0;
        }

        var blockIndex = (int)(offset >> BlockShift);
        var blocks = _blocks;
        if (blockIndex >= blocks.Length)
        {
            var grown = new char[blockIndex + 1][];
            Array.Copy(blocks, grown, blocks.Length);
            for (var i = blocks.Length; i < grown.Length; i++)
            {
                grown[i] = new char[BlockSize];
            }

            Volatile.Write(ref _blocks, grown);
            blocks = grown;
        }

        value.CopyTo(blocks[blockIndex].AsSpan(within));
        Volatile.Write(ref _next, offset + value.Length);
        return offset;
    }

    /// <summary>Appends the case-folded form (<see cref="char.ToUpperInvariant"/>) of <paramref name="value"/> at a known offset
    /// computed by a parallel arena; both arenas must stay in lock step.</summary>
    public long AppendFolded(ReadOnlySpan<char> value)
    {
        Span<char> folded = value.Length <= 512 ? stackalloc char[value.Length] : new char[value.Length];
        Fold(value, folded);
        return Append(folded);
    }

    public ReadOnlySpan<char> Read(long offset, int length)
    {
        var blocks = Volatile.Read(ref _blocks);
        return new ReadOnlySpan<char>(blocks[(int)(offset >> BlockShift)], (int)(offset & BlockMask), length);
    }

    /// <summary>Ordinal invariant upper-case folding used for all filename matching (§7.1).</summary>
    public static void Fold(ReadOnlySpan<char> source, Span<char> destination)
    {
        for (var i = 0; i < source.Length; i++)
        {
            destination[i] = char.ToUpperInvariant(source[i]);
        }
    }

    public static string Fold(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return string.Create(value.Length, value, static (span, v) => Fold(v, span));
    }
}
