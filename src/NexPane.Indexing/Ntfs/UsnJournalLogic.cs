using NexPane.Core.Indexing;
using NexPane.Shell.FileSystem;

namespace NexPane.Indexing.Ntfs;

/// <summary>How to (re)start a volume's journal-driven index (P3.12).</summary>
public enum JournalStartMode
{
    /// <summary>Continue from the persisted USN: the snapshot is still valid.</summary>
    Resume,

    /// <summary>Full re-enumeration: no snapshot, journal recreated/reset, or entries since the snapshot were purged.</summary>
    FullScan,
}

public sealed record JournalStartDecision(JournalStartMode Mode, long StartUsn, string Reason);

/// <summary>Journal resume rules of §7.3: resume from the persisted <c>(journal_id, last_usn)</c>; a different journal id
/// (deleted/recreated journal) or a last USN older than the journal's first retained USN forces a full rescan.</summary>
public static class JournalResume
{
    public static JournalStartDecision Decide(long? persistedJournalId, long? persistedLastUsn, UsnJournalInfo journal)
    {
        if (persistedJournalId is null || persistedLastUsn is null)
        {
            return new JournalStartDecision(JournalStartMode.FullScan, journal.NextUsn, "No persisted journal position.");
        }

        if (unchecked((ulong)persistedJournalId.Value) != journal.JournalId)
        {
            return new JournalStartDecision(JournalStartMode.FullScan, journal.NextUsn,
                $"Journal id changed ({persistedJournalId:X} → {journal.JournalId:X}): the journal was deleted or recreated.");
        }

        if (persistedLastUsn.Value < journal.FirstUsn || persistedLastUsn.Value < journal.LowestValidUsn)
        {
            return new JournalStartDecision(JournalStartMode.FullScan, journal.NextUsn,
                $"Persisted USN {persistedLastUsn} is older than the first retained USN {journal.FirstUsn} (journal wrapped).");
        }

        if (persistedLastUsn.Value > journal.NextUsn)
        {
            return new JournalStartDecision(JournalStartMode.FullScan, journal.NextUsn, "Persisted USN is ahead of the journal (volume restored?).");
        }

        return new JournalStartDecision(JournalStartMode.Resume, persistedLastUsn.Value, "Resuming from the persisted journal position.");
    }
}

/// <summary>Stat lookup for a file reference (OpenFileById), injected so translation is testable.</summary>
public delegate bool StatByReference(ulong fileReference, out uint attributes, out long size, out long createdUtc, out long modifiedUtc);

/// <summary>
/// Translates USN journal records into index deltas (P3.10): creates, renames/moves and stat changes become id-addressed
/// upserts/moves (stat read once per file at its <c>CLOSE</c> record), deletions become removes. Records of the same file
/// within one read are coalesced.
/// </summary>
public static class UsnChangeTranslator
{
    private const UsnReason StatReasons = UsnReason.DataOverwrite | UsnReason.DataExtend | UsnReason.DataTruncation | UsnReason.BasicInfoChange |
                                          UsnReason.FileCreate | UsnReason.CompressionChange | UsnReason.EncryptionChange | UsnReason.ReparsePointChange;

    public static List<IndexDelta> Translate(IReadOnlyList<UsnRecord> records, StatByReference stat)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(stat);
        var deltas = new List<IndexDelta>();
        var pendingStat = new Dictionary<ulong, UsnRecord>();
        foreach (var record in records)
        {
            if (record.FileName is null)
            {
                continue; // V4 range-tracking records carry no name or hierarchy change
            }

            var frn = record.FileReferenceLow;
            if (record.Reason.HasFlag(UsnReason.FileDelete))
            {
                pendingStat.Remove(frn);
                deltas.Add(IndexDelta.RemoveById(frn));
                continue;
            }

            if (record.Reason.HasFlag(UsnReason.RenameNewName))
            {
                deltas.Add(IndexDelta.MoveById(frn, record.ParentReferenceLow, record.FileName));
                pendingStat[frn] = record;
            }
            else if ((record.Reason & StatReasons) != 0)
            {
                pendingStat[frn] = record;
            }

            if (record.Reason.HasFlag(UsnReason.Close) && pendingStat.Remove(frn, out var last))
            {
                deltas.Add(Upsert(last with { Reason = record.Reason }, stat));
            }
        }

        // Files still open at the end of this read: publish what is known now; their CLOSE record refreshes the stat later.
        foreach (var record in pendingStat.Values)
        {
            deltas.Add(Upsert(record, stat));
        }

        return deltas;
    }

    private static IndexDelta Upsert(UsnRecord record, StatByReference stat)
    {
        var frn = record.FileReferenceLow;
        if (stat(frn, out var attributes, out var size, out var created, out var modified))
        {
            return IndexDelta.UpsertById(frn, record.ParentReferenceLow, record.FileName!, attributes, size, modified, created);
        }

        return IndexDelta.UpsertById(frn, record.ParentReferenceLow, record.FileName!, record.FileAttributes, 0, record.TimeStamp, record.TimeStamp);
    }
}

/// <summary>
/// Service-side privacy filter (§7.5, P3.14): drops every entry below a directory the client cannot list. Decisions are
/// cached by security id (entries sharing a descriptor share the verdict) and fall back to the directory path.
/// </summary>
public sealed class AccessFilter(Func<string, bool> canList)
{
    private readonly Dictionary<uint, bool> _bySecurityId = [];

    public int Checks { get; private set; }

    /// <summary>Filters entries (FRN → parent FRN graph) rooted at <paramref name="rootReference"/> with root path
    /// <paramref name="rootPath"/>. <paramref name="securityIdOf"/> returns 0 when unknown.</summary>
    public List<MftStat> Filter(IReadOnlyDictionary<ulong, MftStat> entries, ulong rootReference, string rootPath, Func<ulong, uint> securityIdOf)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var listable = new Dictionary<ulong, bool> { [rootReference] = Check(rootPath, securityIdOf(rootReference)) };
        var paths = new Dictionary<ulong, string> { [rootReference] = rootPath.TrimEnd('\\') + "\\" };
        var result = new List<MftStat>(entries.Count);
        foreach (var entry in entries.Values)
        {
            if (entry.FileReference == rootReference)
            {
                continue;
            }

            if (ParentListable(entry.ParentReference, entries, rootReference, listable, paths, securityIdOf))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    private bool ParentListable(ulong parent, IReadOnlyDictionary<ulong, MftStat> entries, ulong root, Dictionary<ulong, bool> listable,
        Dictionary<ulong, string> paths, Func<ulong, uint> securityIdOf)
    {
        // Walk up to the first decided ancestor, then decide downwards (a directory is listable only if its parent is).
        var chain = new Stack<ulong>();
        var current = parent;
        while (!listable.ContainsKey(current))
        {
            if (!entries.TryGetValue(current, out var dir) || chain.Count > 4096)
            {
                return false; // orphan (not under the root) — fail closed
            }

            chain.Push(current);
            current = dir.ParentReference;
        }

        var allowed = listable[current];
        while (chain.Count > 0)
        {
            var dir = chain.Pop();
            var stat = entries[dir];
            var path = paths[stat.ParentReference] + stat.Name;
            paths[dir] = path + "\\";
            allowed = allowed && Check(path, stat.SecurityId);
            listable[dir] = allowed;
        }

        return allowed;
    }

    private bool Check(string path, uint securityId)
    {
        if (securityId != 0 && _bySecurityId.TryGetValue(securityId, out var cached))
        {
            return cached;
        }

        Checks++;
        var verdict = canList(path);
        if (securityId != 0)
        {
            _bySecurityId[securityId] = verdict;
        }

        return verdict;
    }
}

/// <summary>
/// Journal-side privacy filter (§7.5, P3.14): a change is streamed to the client only when its parent directory and
/// every ancestor up to the volume root are listable by the client, the same rule <see cref="AccessFilter"/> applies to
/// the initial enumeration (a denied directory hides its whole subtree). Verdicts are cached per directory reference and
/// per ancestor path; the caches are cleared when a directory's security descriptor changes, a directory is renamed or
/// moved (into or out of a protected tree) or deleted, and whenever they reach <paramref name="capacity"/> entries.
/// </summary>
public sealed class JournalAccessFilter(Func<string, bool> canList, Func<ulong, string?> directoryPathOf, ulong rootReference, string rootPath,
    int capacity = JournalAccessFilter.DefaultCapacity)
{
    public const int DefaultCapacity = 8192;

    private const UsnReason InvalidatingDirectoryReasons = UsnReason.SecurityChange | UsnReason.RenameOldName | UsnReason.RenameNewName | UsnReason.FileDelete;

    private readonly Dictionary<ulong, bool> _byDirectory = [];
    private readonly Dictionary<string, bool> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root = rootPath.TrimEnd('\\');

    /// <summary>Number of <c>canList</c> calls (diagnostics).</summary>
    public int Checks { get; private set; }

    /// <summary>Number of cache invalidations (security change, directory move/delete, capacity).</summary>
    public int Invalidations { get; private set; }

    public int CachedVerdicts => _byDirectory.Count + _byPath.Count;

    public bool IsVisible(UsnRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.IsDirectory && (record.Reason & InvalidatingDirectoryReasons) != 0)
        {
            Invalidate();
        }

        return DirectoryVisible(record.ParentReferenceLow);
    }

    public void Invalidate()
    {
        if (_byDirectory.Count > 0 || _byPath.Count > 0)
        {
            _byDirectory.Clear();
            _byPath.Clear();
            Invalidations++;
        }
    }

    private bool DirectoryVisible(ulong directory)
    {
        if (_byDirectory.TryGetValue(directory, out var known))
        {
            return known;
        }

        if (CachedVerdicts >= capacity)
        {
            Invalidate();
        }

        string? path = directory == rootReference ? _root : directoryPathOf(directory);
        if (path is null)
        {
            return false; // unresolvable (deleted meanwhile): fail closed, not cached
        }

        var verdict = AncestorsListable(path.TrimEnd('\\'));
        _byDirectory[directory] = verdict;
        return verdict;
    }

    private bool AncestorsListable(string path)
    {
        if (!path.StartsWith(_root, StringComparison.OrdinalIgnoreCase) || (path.Length > _root.Length && path[_root.Length] != '\\'))
        {
            return false; // not below this volume's root: fail closed
        }

        if (!PathListable(_root + "\\"))
        {
            return false;
        }

        if (path.Length <= _root.Length + 1)
        {
            return true; // the root itself
        }

        for (var end = path.IndexOf('\\', _root.Length + 1); ; end = path.IndexOf('\\', end + 1))
        {
            var ancestor = end < 0 ? path : path[..end];
            if (ancestor.Length > _root.Length && !PathListable(ancestor))
            {
                return false;
            }

            if (end < 0)
            {
                return true;
            }
        }
    }

    private bool PathListable(string path)
    {
        if (_byPath.TryGetValue(path, out var known))
        {
            return known;
        }

        Checks++;
        var verdict = canList(path);
        _byPath[path] = verdict;
        return verdict;
    }
}
