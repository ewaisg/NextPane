using NexPane.Core.Indexing;
using NexPane.Indexing.Service;
using NexPane.Indexing.Store;
using NexPane.Shell.FileSystem;
using NexPane.Shell.Security;

namespace NexPane.Indexing.Ntfs;

/// <summary>Persisted journal position of a volume (<c>volumes.usn_journal_id</c>, <c>volumes.last_usn</c>).</summary>
public sealed record JournalPosition(long? JournalId, long? LastUsn);

/// <summary>
/// NTFS fast index source (§7.3, P3.10/P3.12) over the Index Service (<see cref="IndexServiceClient"/>) or, when the app
/// runs elevated, an in-process <see cref="IIndexVolumeBackend"/>. Decides between resuming from the persisted
/// <c>(journal_id, last_usn)</c> and a full re-enumeration (<see cref="JournalResume"/>), applies the enumeration to the
/// segment and then follows the journal. A journal reset reported while following triggers a reset and full rescan.
/// </summary>
public sealed class UsnVolumeSource(IndexStore store, SegmentHandle segment, VolumeInfoMessage volume)
{
    /// <summary>Last applied USN (persist it with the journal id for resume).</summary>
    public long LastUsn { get; private set; }

    public JournalStartDecision? Decision { get; private set; }

    public int FullScans { get; private set; }

    /// <summary>Runs through the Index Service until cancelled.</summary>
    public async Task RunAsync(IndexServiceClient client, JournalPosition persisted, Action<long, long>? persistPosition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(persisted);
        var journal = new UsnJournalInfo(volume.JournalId, 0, volume.NextUsn, 0, long.MaxValue, 0, 0);
        Decision = JournalResume.Decide(persisted.JournalId, persisted.LastUsn, journal);
        var from = Decision.StartUsn;
        while (!ct.IsCancellationRequested)
        {
            var connection = await client.ConnectAsync(ct).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                if (Decision.Mode == JournalStartMode.FullScan)
                {
                    await store.ResetVolumeAsync(segment.Id, ct).ConfigureAwait(false);
                    from = await connection.EnumerateIntoAsync(volume.VolumeGuid, store, segment, ct).ConfigureAwait(false);
                    FullScans++;
                }

                LastUsn = from;
                persistPosition?.Invoke(unchecked((long)volume.JournalId), from);
                try
                {
                    await connection.FollowAsync(volume.VolumeGuid, from, store, segment, usn =>
                    {
                        LastUsn = usn;
                        persistPosition?.Invoke(unchecked((long)volume.JournalId), usn);
                    }, ct).ConfigureAwait(false);
                    return;
                }
                catch (JournalResetException ex)
                {
                    // IndexPipeSession.ErrorJournalReset: the journal wrapped or was reset on the service side.
                    Decision = new JournalStartDecision(JournalStartMode.FullScan, volume.NextUsn, ex.Message);
                }
            }
        }
    }

    /// <summary>Runs against an in-process backend (app elevated) until cancelled.</summary>
    public async Task RunInProcessAsync(IIndexVolumeBackend backend, JournalPosition persisted, Action<long, long>? persistPosition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(persisted);
        // Same privacy rule as the service: only what this user could list (walker parity, e.g. System Volume Information).
        using var access = DirectoryAccessCheck.ForCurrentUser();
        var journal = new UsnJournalInfo(volume.JournalId, 0, volume.NextUsn, 0, long.MaxValue, 0, 0);
        Decision = JournalResume.Decide(persisted.JournalId, persisted.LastUsn, journal);
        var from = Decision.StartUsn;
        while (!ct.IsCancellationRequested)
        {
            if (Decision.Mode == JournalStartMode.FullScan)
            {
                await store.ResetVolumeAsync(segment.Id, ct).ConfigureAwait(false);
                from = await backend.EnumerateAsync(volume.VolumeGuid, access.CanList, batch => store.SubmitAsync(segment.Id, batch.Select(e =>
                    IndexDelta.UpsertById(e.FileReference, e.ParentReference, e.Name, e.Attributes, e.Size, e.ModifiedUtc, e.CreatedUtc)).ToList(), ct).AsTask(), ct).ConfigureAwait(false);
                await store.FlushAsync(ct).ConfigureAwait(false);
                FullScans++;
            }

            LastUsn = from;
            try
            {
                using var volumeHandle = NtfsVolume.Open(volume.Root);
                await backend.SubscribeAsync(volume.VolumeGuid, from, access.CanList, async changes =>
                {
                    var records = changes.Select(c => new UsnRecord(3, 0, c.FileReference, c.ParentReference, c.Usn, 0, (UsnReason)c.Reason, 0, 0,
                        c.Attributes, c.Name, [], 0)).ToList();
                    var deltas = UsnChangeTranslator.Translate(records, (ulong frn, out uint attributes, out long size, out long created, out long modified) =>
                        FileById.TryStat(volumeHandle.Handle, frn, out attributes, out size, out created, out modified));
                    await store.SubmitAsync(segment.Id, deltas, ct).ConfigureAwait(false);
                    LastUsn = changes[^1].Usn;
                    persistPosition?.Invoke(unchecked((long)volume.JournalId), LastUsn);
                }, ct).ConfigureAwait(false);
                return;
            }
            catch (JournalResetException ex)
            {
                Decision = new JournalStartDecision(JournalStartMode.FullScan, volume.NextUsn, ex.Message);
            }
        }
    }
}
