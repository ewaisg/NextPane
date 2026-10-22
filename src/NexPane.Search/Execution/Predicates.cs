using System.Text;
using NexPane.Core.Indexing;

namespace NexPane.Search.Execution;

/// <summary>
/// A compiled, per-execution filter over index entries (§6.2 "memory predicates"). <see cref="Prepare"/> runs once per
/// snapshot before a scan (e.g. directory sets for <c>path:</c>); <see cref="Match"/> is called concurrently from the
/// scan partitions and must be thread-safe and allocation-free on the hot path.
/// </summary>
public abstract class Predicate
{
    /// <summary>Relative evaluation cost used by the planner to order AND/OR operands (cheap first).</summary>
    public abstract int Cost { get; }

    /// <summary>True when the predicate depends on directory paths (must be re-prepared after renames/moves).</summary>
    public virtual bool DependsOnPaths => false;

    public virtual void Prepare(IIndexSnapshot snapshot, CancellationToken ct)
    {
    }

    public abstract bool Match(IndexSegment segment, int index);

    public abstract string Describe();
}

public sealed class ConstantPredicate(bool value) : Predicate
{
    public static ConstantPredicate True { get; } = new(true);

    public static ConstantPredicate False { get; } = new(false);

    public bool Value { get; } = value;

    public override int Cost => 0;

    public override bool Match(IndexSegment segment, int index) => Value;

    public override string Describe() => Value ? "true" : "false";
}

public sealed class AndPredicate(IReadOnlyList<Predicate> operands) : Predicate
{
    private readonly Predicate[] _operands = [.. operands.OrderBy(o => o.Cost)];

    public IReadOnlyList<Predicate> Operands => _operands;

    public override int Cost => _operands.Sum(o => o.Cost);

    public override bool DependsOnPaths => _operands.Any(o => o.DependsOnPaths);

    public override void Prepare(IIndexSnapshot snapshot, CancellationToken ct)
    {
        foreach (var o in _operands)
        {
            o.Prepare(snapshot, ct);
        }
    }

    public override bool Match(IndexSegment segment, int index)
    {
        foreach (var o in _operands)
        {
            if (!o.Match(segment, index))
            {
                return false;
            }
        }

        return true;
    }

    public override string Describe() => "AND[" + string.Join(", ", _operands.Select(o => o.Describe())) + "]";
}

public sealed class OrPredicate(IReadOnlyList<Predicate> operands) : Predicate
{
    private readonly Predicate[] _operands = [.. operands.OrderBy(o => o.Cost)];

    public IReadOnlyList<Predicate> Operands => _operands;

    public override int Cost => _operands.Sum(o => o.Cost);

    public override bool DependsOnPaths => _operands.Any(o => o.DependsOnPaths);

    public override void Prepare(IIndexSnapshot snapshot, CancellationToken ct)
    {
        foreach (var o in _operands)
        {
            o.Prepare(snapshot, ct);
        }
    }

    public override bool Match(IndexSegment segment, int index)
    {
        foreach (var o in _operands)
        {
            if (o.Match(segment, index))
            {
                return true;
            }
        }

        return false;
    }

    public override string Describe() => "OR[" + string.Join(", ", _operands.Select(o => o.Describe())) + "]";
}

public sealed class NotPredicate(Predicate operand) : Predicate
{
    public Predicate Operand { get; } = operand;

    public override int Cost => Operand.Cost;

    public override bool DependsOnPaths => Operand.DependsOnPaths;

    public override void Prepare(IIndexSnapshot snapshot, CancellationToken ct) => Operand.Prepare(snapshot, ct);

    public override bool Match(IndexSegment segment, int index) => !Operand.Match(segment, index);

    public override string Describe() => "NOT " + Operand.Describe();
}

/// <summary>Case-insensitive substring on the folded name (vectorized <c>IndexOf</c>).</summary>
public sealed class NameSubstringPredicate(string foldedNeedle) : Predicate
{
    public string Needle { get; } = foldedNeedle;

    public override int Cost => 2;

    public override bool Match(IndexSegment segment, int index) =>
        segment.GetFoldedName(index).IndexOf(Needle.AsSpan()) >= 0;

    public override string Describe() => "name~" + Needle;
}

public sealed class NameGlobPredicate(string foldedPattern) : Predicate
{
    private readonly GlobMatcher _glob = new(foldedPattern);

    public string Pattern { get; } = foldedPattern;

    public override int Cost => 3;

    public override bool Match(IndexSegment segment, int index) => _glob.IsMatch(segment.GetFoldedName(index));

    public override string Describe() => "glob:" + Pattern;
}

public sealed class NameRegexPredicate(UserRegex regex) : Predicate
{
    private int _timeouts;

    public UserRegex Regex { get; } = regex;

    public int Timeouts => Volatile.Read(ref _timeouts);

    public override int Cost => Regex.SlowMode ? 40 : 8;

    public override bool Match(IndexSegment segment, int index) => Regex.IsMatch(segment.GetName(index), ref _timeouts);

    public override string Describe() => (Regex.SlowMode ? "regex(slow):" : "regex:") + Regex.Regex;
}

/// <summary><c>ext:</c> — folded extension in a set (files only).</summary>
public sealed class ExtensionPredicate : Predicate
{
    private readonly HashSet<string> _extensions;
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    public ExtensionPredicate(IEnumerable<string> foldedExtensions, string description)
    {
        _extensions = new HashSet<string>(foldedExtensions, StringComparer.Ordinal);
        _lookup = _extensions.GetAlternateLookup<ReadOnlySpan<char>>();
        Description = description;
    }

    public string Description { get; }

    public override int Cost => 1;

    public override bool Match(IndexSegment segment, int index) =>
        !segment.IsDirectory(index) && _lookup.Contains(segment.GetFoldedExtension(index));

    public override string Describe() => Description;
}

public enum NumericColumn
{
    Size,
    Modified,
    Created,
    Length,
    Depth,
}

/// <summary><c>size:</c>, <c>modified:</c>, <c>created:</c>, <c>len:</c>, <c>depth:</c> — value in an interval set.</summary>
public sealed class NumericPredicate(NumericColumn column, IntervalSet set, string description) : Predicate
{
    public NumericColumn Column { get; } = column;

    public IntervalSet Set { get; } = set;

    public override int Cost => Column == NumericColumn.Depth ? 3 : 1;

    public override bool Match(IndexSegment segment, int index)
    {
        switch (Column)
        {
            case NumericColumn.Size:
                return !segment.IsDirectory(index) && Set.Contains(segment.GetSize(index));
            case NumericColumn.Modified:
            {
                var value = segment.GetModified(index);
                return value != 0 && Set.Contains(value);
            }

            case NumericColumn.Created:
            {
                var value = segment.GetCreated(index);
                return value != 0 && Set.Contains(value);
            }

            case NumericColumn.Length:
                return Set.Contains(segment.GetNameLength(index));
            case NumericColumn.Depth:
                return Set.Contains(segment.GetDepth(index));
            default:
                return false;
        }
    }

    public override string Describe() => description;
}

/// <summary><c>attr:</c> — Win32 attribute bits (all of <see cref="Mask"/> set, or none when negated).</summary>
public sealed class AttributePredicate(uint mask, bool negate, string description) : Predicate
{
    public uint Mask { get; } = mask;

    public bool Negate { get; } = negate;

    public override int Cost => 1;

    public override bool Match(IndexSegment segment, int index)
    {
        var has = (segment.GetAttributes(index) & Mask) == Mask;
        return Negate ? !has : has;
    }

    public override string Describe() => description;
}

/// <summary><c>type:folder</c> / <c>type:file</c>.</summary>
public sealed class KindPredicate(bool directories) : Predicate
{
    public override int Cost => 1;

    public override bool Match(IndexSegment segment, int index) => segment.IsDirectory(index) == directories;

    public override string Describe() => directories ? "type:folder" : "type:file";
}

/// <summary>Set of entries known by identity (FRN on NTFS volumes) or by folded full path — used for tags, FTS hits,
/// semantic hits.</summary>
public sealed class EntryKeySet
{
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _names;

    public EntryKeySet(IEnumerable<(ulong VolumeSerial, ulong FileId)> ids, IEnumerable<string> fullPaths)
    {
        Ids = [.. ids];
        FoldedPaths = new HashSet<string>(fullPaths.Select(p => NameArena.Fold(TrimSeparator(p))), StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in FoldedPaths)
        {
            var cut = path.LastIndexOfAny(['\\', '/']);
            names.Add(cut < 0 ? path : path[(cut + 1)..]);
        }

        FoldedNames = names;
        _names = names.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public static EntryKeySet Empty { get; } = new([], []);

    public HashSet<(ulong VolumeSerial, ulong FileId)> Ids { get; }

    public HashSet<string> FoldedPaths { get; }

    public HashSet<string> FoldedNames { get; }

    public int Count => Ids.Count + FoldedPaths.Count;

    public bool Contains(IndexSegment segment, int index)
    {
        var volume = segment.Volume;
        if (Ids.Count > 0 && volume.HasStableFileIds && Ids.Contains((volume.VolumeSerial, segment.GetFileId(index))))
        {
            return true;
        }

        if (FoldedPaths.Count == 0 || !_names.Contains(segment.GetFoldedName(index)))
        {
            return false;
        }

        return FoldedPaths.Contains(NameArena.Fold(segment.GetFullPath(index)));
    }

    private static string TrimSeparator(string path) =>
        path.Length > 3 && (path.EndsWith('\\') || path.EndsWith('/')) ? path[..^1] : path;
}

/// <summary>Membership in an <see cref="EntryKeySet"/> that is resolved asynchronously before the scan.</summary>
public sealed class SetPredicate(string description) : Predicate
{
    private EntryKeySet _set = EntryKeySet.Empty;

    public EntryKeySet Set
    {
        get => _set;
        set => _set = value ?? EntryKeySet.Empty;
    }

    public override int Cost => 3;

    public override bool Match(IndexSegment segment, int index) => _set.Contains(segment, index);

    public override string Describe() => description;
}

/// <summary>
/// <c>path:</c> substring (§7.2): one top-down pass over directories computes whether each directory's folded full path
/// contains the needle plus the last <c>needle.Length − 1</c> characters of that path; an entry then matches when its
/// parent's path contains the needle or when the window "parent suffix + separator + name" does. O(n) per query.
/// </summary>
public sealed class PathSubstringPredicate(string foldedNeedle) : Predicate
{
    private SegmentState?[] _states = [];

    public string Needle { get; } = foldedNeedle;

    public override int Cost => 4;

    public override bool DependsOnPaths => true;

    public override void Prepare(IIndexSnapshot snapshot, CancellationToken ct)
    {
        var states = new SegmentState?[snapshot.SegmentCount];
        for (var s = 0; s < states.Length; s++)
        {
            if (snapshot is IndexGeneration g && !g.HasSegment(s))
            {
                continue;
            }

            states[s] = Build(snapshot.GetSegment(s), snapshot.Count(s), ct);
        }

        _states = states;
    }

    public override bool Match(IndexSegment segment, int index)
    {
        var state = segment.Id < _states.Length ? _states[segment.Id] : null;
        if (state is null || index >= state.Contains.Length)
        {
            return false;
        }

        if (index == 0)
        {
            return state.Contains[0];
        }

        var parent = segment.GetParent(index);
        if (parent < state.Contains.Length && state.Contains[parent])
        {
            return true;
        }

        var suffix = parent < state.Suffix.Length ? state.Suffix[parent] ?? string.Empty : string.Empty;
        return WindowContains(suffix, state.NeedsSeparator(parent), segment.Volume.Separator, segment.GetFoldedName(index), Needle);
    }

    public override string Describe() => "path~" + Needle;

    private SegmentState Build(IndexSegment segment, int count, CancellationToken ct)
    {
        var contains = new bool[count];
        var suffix = new string?[count];
        var computed = new bool[count];
        var rootFolded = NameArena.Fold(segment.Volume.RootPath);
        var separator = segment.Volume.Separator;
        var keep = Math.Max(0, Needle.Length - 1);
        contains[0] = rootFolded.Contains(Needle, StringComparison.Ordinal);
        suffix[0] = rootFolded.Length > keep ? rootFolded[^keep..] : rootFolded;
        computed[0] = true;
        var rootEndsWithSeparator = rootFolded.Length > 0 && rootFolded[^1] == separator;
        var stack = new Stack<int>();
        for (var i = 1; i < count; i++)
        {
            if ((i & 0x3FF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            if (computed[i] || !segment.IsDirectory(i) || segment.IsDeleted(i))
            {
                continue;
            }

            var current = i;
            while (!computed[current])
            {
                stack.Push(current);
                var parent = segment.GetParent(current);
                if (parent == current || parent >= count || stack.Count > 4096)
                {
                    break;
                }

                current = parent;
            }

            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                var parent = segment.GetParent(dir);
                if (parent >= count || !computed[parent])
                {
                    computed[dir] = true;
                    continue;
                }

                var parentSuffix = suffix[parent] ?? string.Empty;
                var needsSeparator = parent != 0 || !rootEndsWithSeparator;
                var name = segment.GetFoldedName(dir);
                contains[dir] = contains[parent] || WindowContains(parentSuffix, needsSeparator, separator, name, Needle);
                suffix[dir] = Tail(parentSuffix, needsSeparator, separator, name, keep);
                computed[dir] = true;
            }
        }

        return new SegmentState(contains, suffix, rootEndsWithSeparator);
    }

    internal static bool WindowContains(string parentSuffix, bool needsSeparator, char separator, ReadOnlySpan<char> name, string needle)
    {
        var length = parentSuffix.Length + (needsSeparator ? 1 : 0) + name.Length;
        Span<char> buffer = length <= 1024 ? stackalloc char[length] : new char[length];
        parentSuffix.AsSpan().CopyTo(buffer);
        var pos = parentSuffix.Length;
        if (needsSeparator)
        {
            buffer[pos++] = separator;
        }

        name.CopyTo(buffer[pos..]);
        return buffer.IndexOf(needle, StringComparison.Ordinal) >= 0;
    }

    private static string Tail(string parentSuffix, bool needsSeparator, char separator, ReadOnlySpan<char> name, int keep)
    {
        if (keep == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(parentSuffix.Length + 1 + name.Length);
        sb.Append(parentSuffix);
        if (needsSeparator)
        {
            sb.Append(separator);
        }

        sb.Append(name);
        return sb.Length > keep ? sb.ToString(sb.Length - keep, keep) : sb.ToString();
    }

    private sealed record SegmentState(bool[] Contains, string?[] Suffix, bool RootEndsWithSeparator)
    {
        public bool NeedsSeparator(int parent) => parent != 0 || !RootEndsWithSeparator;
    }
}

/// <summary><c>path:/regex/</c> — regex over the full path; directory paths are reconstructed once per query.</summary>
public sealed class PathRegexPredicate(UserRegex regex) : Predicate
{
    [ThreadStatic]
    private static char[]? _buffer;

    private string?[][] _dirPaths = [];
    private int _timeouts;

    public UserRegex Regex { get; } = regex;

    public override int Cost => 30;

    public override bool DependsOnPaths => true;

    public override void Prepare(IIndexSnapshot snapshot, CancellationToken ct)
    {
        var all = new string?[snapshot.SegmentCount][];
        for (var s = 0; s < all.Length; s++)
        {
            if (snapshot is IndexGeneration g && !g.HasSegment(s))
            {
                all[s] = [];
                continue;
            }

            var segment = snapshot.GetSegment(s);
            var count = snapshot.Count(s);
            var paths = new string?[count];
            for (var i = 0; i < count; i++)
            {
                if ((i & 0x3FF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                if (segment.IsDirectory(i) && !segment.IsDeleted(i))
                {
                    paths[i] = segment.GetDirectoryPath(i);
                }
            }

            all[s] = paths;
        }

        _dirPaths = all;
    }

    public override bool Match(IndexSegment segment, int index)
    {
        var paths = segment.Id < _dirPaths.Length ? _dirPaths[segment.Id] : [];
        if (index == 0)
        {
            return Regex.IsMatch(segment.Volume.RootPath, ref _timeouts);
        }

        var parent = segment.GetParent(index);
        var parentPath = parent < paths.Length ? paths[parent] ?? segment.GetDirectoryPath(parent) : segment.GetDirectoryPath(parent);
        var name = segment.GetName(index);
        var separator = segment.Volume.Separator;
        var needsSeparator = parentPath.Length > 0 && parentPath[^1] != separator;
        var length = parentPath.Length + (needsSeparator ? 1 : 0) + name.Length;
        var buffer = _buffer;
        if (buffer is null || buffer.Length < length)
        {
            buffer = new char[Math.Max(length, 1024)];
            _buffer = buffer;
        }

        parentPath.AsSpan().CopyTo(buffer);
        var pos = parentPath.Length;
        if (needsSeparator)
        {
            buffer[pos++] = separator;
        }

        name.CopyTo(buffer.AsSpan(pos));
        return Regex.IsMatch(buffer.AsSpan(0, length), ref _timeouts);
    }

    public override string Describe() => "path-regex:" + Regex.Regex;
}

/// <summary>Entries whose ancestor chain contains one of the target directories (<c>parent:</c> uses
/// <see cref="DirectChildrenOnly"/>; search scopes use the whole subtree).</summary>
public sealed class UnderDirectoryPredicate(IReadOnlyList<string> directoryPaths, bool directChildrenOnly, string description) : Predicate
{
    private readonly string[] _targets = [.. directoryPaths.Select(p => NameArena.Fold(TrimEndSeparator(p)))];
    private byte[]?[] _marks = [];

    public bool DirectChildrenOnly { get; } = directChildrenOnly;

    public override int Cost => 2;

    public override bool DependsOnPaths => true;

    public override void Prepare(IIndexSnapshot snapshot, CancellationToken ct)
    {
        var marks = new byte[]?[snapshot.SegmentCount];
        for (var s = 0; s < marks.Length; s++)
        {
            if (snapshot is IndexGeneration g && !g.HasSegment(s))
            {
                continue;
            }

            marks[s] = Build(snapshot.GetSegment(s), snapshot.Count(s), ct);
        }

        _marks = marks;
    }

    // marks: 0 unknown, 1 target dir, 2 inside (strict descendant of a target), 3 outside
    public override bool Match(IndexSegment segment, int index)
    {
        var marks = segment.Id < _marks.Length ? _marks[segment.Id] : null;
        if (marks is null || index == 0)
        {
            return false;
        }

        var parent = segment.GetParent(index);
        if (parent >= marks.Length)
        {
            return false;
        }

        return DirectChildrenOnly ? marks[parent] == 1 : marks[parent] is 1 or 2;
    }

    public override string Describe() => description;

    private byte[] Build(IndexSegment segment, int count, CancellationToken ct)
    {
        var marks = new byte[count];
        var lastNames = new HashSet<string>(_targets.Select(LastComponent), StringComparer.Ordinal).GetAlternateLookup<ReadOnlySpan<char>>();
        var rootFolded = NameArena.Fold(TrimEndSeparator(segment.Volume.RootPath));
        marks[0] = _targets.Contains(rootFolded, StringComparer.Ordinal) ? (byte)1 : (byte)3;
        for (var i = 1; i < count; i++)
        {
            if ((i & 0x3FF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            if (segment.IsDirectory(i) && !segment.IsDeleted(i) && lastNames.Contains(segment.GetFoldedName(i)) &&
                _targets.Contains(NameArena.Fold(segment.GetDirectoryPath(i)), StringComparer.Ordinal))
            {
                marks[i] = 1;
            }
        }

        if (DirectChildrenOnly)
        {
            return marks;
        }

        var stack = new Stack<int>();
        for (var i = 1; i < count; i++)
        {
            if (marks[i] != 0 || !segment.IsDirectory(i))
            {
                continue;
            }

            var current = i;
            while (marks[current] == 0)
            {
                stack.Push(current);
                var parent = segment.GetParent(current);
                if (parent == current || parent >= count || stack.Count > 4096)
                {
                    marks[current] = 3;
                    stack.Pop();
                    break;
                }

                current = parent;
            }

            var verdict = marks[current] is 1 or 2 ? (byte)2 : (byte)3;
            while (stack.Count > 0)
            {
                marks[stack.Pop()] = verdict;
            }
        }

        return marks;
    }

    private static string LastComponent(string path)
    {
        var cut = path.LastIndexOfAny(['\\', '/']);
        return cut < 0 || cut == path.Length - 1 ? path : path[(cut + 1)..];
    }

    private static string TrimEndSeparator(string path) =>
        path.Length > 1 && (path.EndsWith('\\') || path.EndsWith('/')) && !(path.Length == 3 && path[1] == ':') ? path[..^1] : path;
}
