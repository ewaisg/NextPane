using System.Buffers;
using System.Runtime.CompilerServices;
using NexPane.Core.Indexing;

namespace NexPane.Search.Execution;

/// <summary>
/// Folded names of a result set, stored contiguously per scan partition (<c>name\0name\0…</c>) together with the entry
/// indexes (§7.2 incremental refinement). A keystroke refinement runs vectorized <c>IndexOf</c> over a few MB of
/// sequential memory instead of fetching every candidate's name through the index columns. Refined results do not copy
/// names: they select item numbers of the same root buffer, so a whole typing sequence shares one buffer. Valid only for
/// the generation it was built from (every index change publishes a new generation, and refinement requires the same
/// generation).
/// </summary>
internal sealed class RefinementBuffer
{
    /// <summary>Above this many results no buffer is kept (memory bound; broader sets refine through the index).</summary>
    public const int MaxItems = 400_000;

    /// <summary>Total item count above which chunks are refined in parallel.</summary>
    private const int ParallelThreshold = 24_576;

    private const char Separator = '\0';

    private RefinementBuffer(Chunk[] chunks)
    {
        Chunks = chunks;
        var total = 0;
        foreach (var chunk in chunks)
        {
            total += chunk.Count;
        }

        Count = total;
    }

    public Chunk[] Chunks { get; }

    public int Count { get; }

    public static RefinementBuffer? Create(Chunk[] chunks)
    {
        var buffer = new RefinementBuffer(chunks);
        return buffer.Count > MaxItems ? null : buffer;
    }

    /// <summary>Filters the buffer with a name-only predicate and returns the refined buffer and its entry refs.</summary>
    public RefinementBuffer Refine(Predicate predicate, out List<EntryRef> items, CancellationToken ct)
    {
        var needle = RequiredSubstring(predicate, out var exact);
        var result = new Chunk[Chunks.Length];
        if (Count < ParallelThreshold)
        {
            for (var i = 0; i < Chunks.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                result[i] = Chunks[i].Filter(predicate, needle, exact);
            }
        }
        else
        {
            Parallel.For(0, Chunks.Length, new ParallelOptions { CancellationToken = ct }, i => result[i] = Chunks[i].Filter(predicate, needle, exact));
        }

        var refined = new RefinementBuffer(result);
        items = refined.ToList();
        return refined;
    }

    /// <summary>Entry refs in index order.</summary>
    public List<EntryRef> ToList()
    {
        var list = new List<EntryRef>(Count);
        foreach (var chunk in Chunks)
        {
            chunk.AppendRefs(list);
        }

        return list;
    }

    /// <summary>A literal every match must contain (a name substring term, alone or inside an AND), for the
    /// vectorized scan. <paramref name="exact"/> is true when that substring is the whole predicate.</summary>
    private static string? RequiredSubstring(Predicate predicate, out bool exact)
    {
        exact = false;
        switch (predicate)
        {
            case NameSubstringPredicate s when s.Needle.Length > 0 && !s.Needle.Contains(Separator, StringComparison.Ordinal):
                exact = true;
                return s.Needle;
            case AndPredicate and:
                return and.Operands.OfType<NameSubstringPredicate>().Select(s => s.Needle)
                    .Where(n => n.Length > 0 && !n.Contains(Separator, StringComparison.Ordinal))
                    .OrderByDescending(n => n.Length).FirstOrDefault();
            default:
                return null;
        }
    }

    /// <summary>Immutable names of one partition's matches at the time the buffer was built.</summary>
    internal sealed class Root(int segment, int count, int[] indexes, int[] starts, char[] chars, bool[] directories)
    {
        public int Segment { get; } = segment;

        public int Count { get; } = count;

        public int[] Indexes { get; } = indexes;

        /// <summary><c>Starts[k]</c> is the offset of item k; <c>Starts[Count]</c> the used length (names are separator-terminated).</summary>
        public int[] Starts { get; } = starts;

        public char[] Chars { get; } = chars;

        public bool[] Directories { get; } = directories;

        public ReadOnlySpan<char> Name(int k) => Chars.AsSpan(Starts[k], Starts[k + 1] - Starts[k] - 1);
    }

    /// <summary>A selection of root items (null selection = all of them).</summary>
    internal sealed class Chunk(Root root, int[]? selected, int count)
    {
        public Root Root { get; } = root;

        public int Count { get; } = count;

        public int Segment => Root.Segment;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void AppendRefs(List<EntryRef> list)
        {
            var indexes = Root.Indexes;
            if (selected is null)
            {
                for (var k = 0; k < Count; k++)
                {
                    list.Add(new EntryRef(Root.Segment, indexes[k]));
                }

                return;
            }

            for (var k = 0; k < Count; k++)
            {
                list.Add(new EntryRef(Root.Segment, indexes[selected[k]]));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public Chunk Filter(Predicate predicate, string? needle, bool exact)
        {
            if (Count == 0)
            {
                return this;
            }

            var output = new int[Count];
            var produced = 0;
            if (needle is not null && (selected is null || Count * 4 >= Root.Count))
            {
                produced = FilterDense(predicate, needle, exact, output);
            }
            else
            {
                for (var k = 0; k < Count; k++)
                {
                    var r = selected is null ? k : selected[k];
                    var name = Root.Name(r);
                    if (needle is not null && name.IndexOf(needle.AsSpan()) < 0)
                    {
                        continue;
                    }

                    if ((needle is not null && exact) || predicate.MatchName(name, Root.Directories[r]))
                    {
                        output[produced++] = r;
                    }
                }
            }

            return new Chunk(Root, output, produced);
        }

        /// <summary>Vectorized scan of the whole root text, jumping from occurrence to occurrence of the needle; hits
        /// in items outside the current selection are skipped.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private int FilterDense(Predicate predicate, string needle, bool exact, int[] output)
        {
            var starts = Root.Starts;
            var text = Root.Chars.AsSpan(0, starts[Root.Count]);
            bool[]? member = null;
            if (selected is not null)
            {
                member = ArrayPool<bool>.Shared.Rent(Root.Count);
                Array.Clear(member, 0, Root.Count);
                for (var k = 0; k < Count; k++)
                {
                    member[selected[k]] = true;
                }
            }

            try
            {
                var produced = 0;
                var position = 0;
                var item = 0;
                while (position < text.Length)
                {
                    var found = text[position..].IndexOf(needle.AsSpan());
                    if (found < 0)
                    {
                        break;
                    }

                    var absolute = position + found;
                    while (starts[item + 1] <= absolute)
                    {
                        item++; // hits are ascending, so the owning item is close: advance linearly
                    }

                    if ((member is null || member[item]) && (exact || predicate.MatchName(Root.Name(item), Root.Directories[item])))
                    {
                        output[produced++] = item;
                    }

                    position = starts[item + 1];
                    item++;
                }

                return produced;
            }
            finally
            {
                if (member is not null)
                {
                    ArrayPool<bool>.Shared.Return(member);
                }
            }
        }
    }

    /// <summary>Growable root builder (one per partition; single-threaded).</summary>
    internal sealed class Builder(int segment, int itemCapacity = 64, int charCapacity = 1024)
    {
        private int[] _indexes = new int[itemCapacity];
        private int[] _starts = new int[itemCapacity + 1];
        private bool[] _directories = new bool[itemCapacity];
        private char[] _chars = new char[charCapacity];
        private int _count;
        private int _length;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Add(int index, ReadOnlySpan<char> foldedName, bool isDirectory)
        {
            if (_count == _indexes.Length)
            {
                var size = _indexes.Length * 2;
                Array.Resize(ref _indexes, size);
                Array.Resize(ref _starts, size + 1);
                Array.Resize(ref _directories, size);
            }

            if (_length + foldedName.Length + 1 > _chars.Length)
            {
                Array.Resize(ref _chars, Math.Max(_chars.Length * 2, _length + foldedName.Length + 1));
            }

            _indexes[_count] = index;
            _directories[_count] = isDirectory;
            _starts[_count] = _length;
            foldedName.CopyTo(_chars.AsSpan(_length));
            _length += foldedName.Length;
            _chars[_length++] = Separator;
            _count++;
            _starts[_count] = _length;
        }

        public Chunk Build() => new(new Root(segment, _count, _indexes, _starts, _chars, _directories), null, _count);
    }
}
