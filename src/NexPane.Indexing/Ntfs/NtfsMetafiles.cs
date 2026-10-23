namespace NexPane.Indexing.Ntfs;

/// <summary>
/// NTFS system metafiles are not user files and never appear in a directory listing, so the walker never indexes them.
/// For parity, the MFT/USN path excludes them by identity, not by name (users may create files named <c>$foo</c>):
/// the reserved records 0–23 (<c>$MFT</c>, <c>$MFTMirr</c>, <c>$LogFile</c>, <c>$Volume</c>, <c>$AttrDef</c>,
/// <c>$Bitmap</c>, <c>$Boot</c>, <c>$BadClus</c>, <c>$Secure</c>, <c>$UpCase</c>, <c>$Extend</c> and the reserved
/// range) except the root directory (record 5), and everything below <c>$Extend</c> (record 11:
/// <c>$Quota</c>, <c>$ObjId</c>, <c>$Reparse</c>, <c>$UsnJrnl</c>, <c>$RmMetadata\…</c>, <c>$Deleted</c>).
/// </summary>
public static class NtfsMetafiles
{
    public const ulong RootRecord = 5;
    public const ulong ExtendRecord = 11;
    public const ulong FirstUserRecord = 24;
    private const ulong RecordMask = 0x0000FFFFFFFFFFFF;

    /// <summary>True for a reserved metafile record (by record number; the root directory is not one).</summary>
    public static bool IsReserved(ulong fileReference)
    {
        var record = fileReference & RecordMask;
        return record < FirstUserRecord && record != RootRecord;
    }

    /// <summary>Removes metafiles and the whole <c>$Extend</c> subtree from an MFT enumeration.</summary>
    public static Dictionary<ulong, MftStat> Exclude(IReadOnlyDictionary<ulong, MftStat> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var verdicts = new Dictionary<ulong, bool>(); // file reference → excluded
        var result = new Dictionary<ulong, MftStat>(entries.Count);
        foreach (var (reference, stat) in entries)
        {
            if (!IsExcluded(reference, entries, verdicts))
            {
                result[reference] = stat;
            }
        }

        return result;
    }

    /// <summary>True when a journal change concerns a metafile: a reserved record, a child of <c>$Extend</c>, or a child
    /// of a directory already known to be excluded.</summary>
    public static bool IsExcludedChange(ulong fileReference, ulong parentReference, IReadOnlySet<ulong> excludedDirectories)
    {
        ArgumentNullException.ThrowIfNull(excludedDirectories);
        return IsReserved(fileReference) || (parentReference & RecordMask) == ExtendRecord || excludedDirectories.Contains(parentReference);
    }

    /// <summary>Directories excluded by <see cref="Exclude"/> (for filtering later journal changes).</summary>
    public static HashSet<ulong> ExcludedDirectories(IReadOnlyDictionary<ulong, MftStat> all, IReadOnlyDictionary<ulong, MftStat> kept)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(kept);
        return [.. all.Where(p => p.Value.IsDirectory && !kept.ContainsKey(p.Key)).Select(p => p.Key)];
    }

    private static bool IsExcluded(ulong reference, IReadOnlyDictionary<ulong, MftStat> entries, Dictionary<ulong, bool> verdicts)
    {
        var chain = new List<ulong>();
        var current = reference;
        bool verdict;
        while (true)
        {
            if (verdicts.TryGetValue(current, out verdict))
            {
                break;
            }

            if ((current & RecordMask) == ExtendRecord || IsReserved(current))
            {
                verdict = true;
                break;
            }

            if ((current & RecordMask) == RootRecord || !entries.TryGetValue(current, out var stat) || chain.Count > 4096)
            {
                verdict = false;
                break;
            }

            chain.Add(current);
            current = stat.ParentReference;
        }

        foreach (var item in chain)
        {
            verdicts[item] = verdict;
        }

        return verdict;
    }
}
