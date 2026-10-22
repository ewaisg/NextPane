using NexPane.Indexing.Service;
using NexPane.Shell.FileSystem;

namespace NexPane.Indexing.Ntfs;

/// <summary>
/// Real Index Service backend (P3.10–P3.14): lists fixed NTFS volumes (creating the change journal when absent,
/// 32 MB / 8 MB delta), enumerates a volume from the raw $MFT filtered by the client's <c>canList</c> check, and tails
/// <c>FSCTL_READ_USN_JOURNAL</c> every 250 ms, dropping changes inside folders the client cannot list. Requires
/// administrator rights (LocalSystem service, or the app running elevated in-process).
/// </summary>
public sealed class NtfsVolumeBackend : IIndexVolumeBackend
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, HashSet<ulong>> _excludedDirectories = new(StringComparer.OrdinalIgnoreCase);

    public const int BatchSize = 4096;
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public IReadOnlyList<VolumeInfoMessage> ListVolumes()
    {
        var result = new List<VolumeInfoMessage>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var facts = VolumeFacts.TryGet(drive.RootDirectory.FullName);
            if (facts?.VolumeGuidPath is null)
            {
                continue;
            }

            using var volume = NtfsVolume.Open(facts.RootPath);
            if (!volume.TryQueryJournal(out var journal, out var error))
            {
                if (error != NtfsVolume.ErrorJournalNotActive)
                {
                    continue;
                }

                volume.CreateJournal();
                if (!volume.TryQueryJournal(out journal, out _))
                {
                    continue;
                }
            }

            FileIdentity.TryGet(facts.RootPath, out var root);
            result.Add(new VolumeInfoMessage(facts.VolumeGuidPath, facts.RootPath, facts.Serial, facts.FileSystem, journal.JournalId, journal.NextUsn, root.FileIdLow));
        }

        return result;
    }

    public async Task<long> EnumerateAsync(string volumeGuid, Func<string, bool> canList, Func<IReadOnlyList<WireEntry>, Task> sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(canList);
        ArgumentNullException.ThrowIfNull(sink);
        var info = Find(volumeGuid);
        using var volume = NtfsVolume.Open(info.Root);
        volume.TryQueryJournal(out var journal, out _);
        var all = await Task.Run(() => MftReader.ReadAll(volume, ct), ct).ConfigureAwait(false);
        var mft = NtfsMetafiles.Exclude(all); // $MFT, $LogFile, $Extend subtree ($Quota, $UsnJrnl, ...) are not user files (walker parity)
        _excludedDirectories[volumeGuid] = NtfsMetafiles.ExcludedDirectories(all, mft);
        var filter = new AccessFilter(canList);
        var visible = filter.Filter(mft, info.RootFileReference, info.Root, frn => mft.TryGetValue(frn, out var s) ? s.SecurityId : 0);
        var batch = new List<WireEntry>(BatchSize);
        foreach (var entry in visible)
        {
            if (entry.Name is null)
            {
                continue;
            }

            batch.Add(new WireEntry(entry.FileReference, entry.ParentReference, entry.Attributes, entry.Size, entry.ModifiedUtc, entry.CreatedUtc, entry.Name));
            if (batch.Count == BatchSize)
            {
                await sink(batch).ConfigureAwait(false);
                batch = new List<WireEntry>(BatchSize);
            }
        }

        if (batch.Count > 0)
        {
            await sink(batch).ConfigureAwait(false);
        }

        return journal.NextUsn;
    }

    public async Task SubscribeAsync(string volumeGuid, long fromUsn, Func<string, bool> canList, Func<IReadOnlyList<WireChange>, Task> sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(canList);
        ArgumentNullException.ThrowIfNull(sink);
        var info = Find(volumeGuid);
        using var volume = NtfsVolume.Open(info.Root);
        var verdicts = new Dictionary<ulong, bool> { [info.RootFileReference] = canList(info.Root) };
        var excluded = _excludedDirectories.GetValueOrDefault(volumeGuid) ?? [];
        var buffer = new byte[64 * 1024];
        var next = fromUsn;
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            while (true)
            {
                var result = volume.ReadJournal(next, info.JournalId, buffer);
                if (result.Error == NtfsVolume.ErrorJournalEntryDeleted)
                {
                    throw new InvalidDataException("The change journal no longer contains the requested USN; a full rescan is required.");
                }

                if (!result.Succeeded)
                {
                    throw new IOException($"FSCTL_READ_USN_JOURNAL failed with Win32 error {result.Error}.", result.Error);
                }

                var parsed = UsnRecordParser.ParseBuffer(buffer.AsSpan(0, result.BytesReturned));
                var changes = new List<WireChange>();
                foreach (var record in parsed.Records)
                {
                    if (record.FileName is null || NtfsMetafiles.IsExcludedChange(record.FileReferenceLow, record.ParentReferenceLow, excluded) ||
                        !Listable(volume, record.ParentReferenceLow, verdicts, canList))
                    {
                        continue;
                    }

                    changes.Add(new WireChange((uint)record.Reason, record.FileReferenceLow, record.ParentReferenceLow, record.FileAttributes, record.FileName, record.Usn));
                }

                if (changes.Count > 0)
                {
                    await sink(changes).ConfigureAwait(false);
                }

                if (parsed.Next == next || parsed.Records.Count == 0)
                {
                    break;
                }

                next = parsed.Next;
            }
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    private static bool Listable(NtfsVolume volume, ulong parent, Dictionary<ulong, bool> verdicts, Func<string, bool> canList)
    {
        if (verdicts.TryGetValue(parent, out var known))
        {
            return known;
        }

        var path = FileReferencePath.TryGet(volume.Handle, parent);
        var verdict = path is not null && canList(path);
        verdicts[parent] = verdict;
        return verdict;
    }

    private VolumeInfoMessage Find(string volumeGuid) =>
        ListVolumes().FirstOrDefault(v => string.Equals(v.VolumeGuid, volumeGuid, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidDataException($"Volume {volumeGuid} is not an NTFS fixed volume.");
}
