using NexPane.Core.Indexing;

namespace NexPane.Indexing.Sources;

/// <summary>Index Service reachability as seen by the app (§7.5).</summary>
public enum IndexServiceState
{
    NotInstalled,
    Stopped,
    Running,

    /// <summary>Running, but the protocol version differs ("Index service outdated").</summary>
    Outdated,
}

/// <summary>Probes the NexPane Index Service pipe (implemented by <see cref="Service.IndexServiceClient"/>).</summary>
public interface IIndexServiceProbe
{
    Task<IndexServiceState> ProbeAsync(CancellationToken ct);
}

/// <summary>Whether this process runs elevated (in-process USN access is then possible).</summary>
public interface IProcessElevation
{
    bool IsElevated { get; }
}

/// <summary>Real elevation check of the current process token.</summary>
public sealed class CurrentProcessElevation : IProcessElevation
{
    public bool IsElevated
    {
        get
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }
}

/// <summary>What a volume is described by when choosing its source.</summary>
public sealed record VolumeCandidate(string RootPath, string FileSystem, bool IsNetwork, bool IsRemovable, bool SupportsUsnJournal);

/// <summary>The chosen source and the UI-visible explanation (P3.17, P4.16).</summary>
public sealed record SourceDecision(VolumeSourceKind Source, VolumeKind Kind, string Detail, string? Hint = null)
{
    public VolumeStatus ToStatus(VolumeScanState state, long entries) => new(string.Empty, Kind, Source, state, entries, Detail, Hint);
}

/// <summary>
/// Source selection (§7.3): network locations → crawler; NTFS fixed volumes → USN via the Index Service when it is
/// running and current, else in-process USN when the app is elevated, else the parallel walker with a hint that the
/// service is missing / stopped / outdated; FAT32/exFAT/ReFS and removable media → walker.
/// </summary>
public sealed class VolumeSourceSelector(IIndexServiceProbe service, IProcessElevation elevation)
{
    public const string ServiceMissingHint = "Install the NexPane Index Service (fast NTFS indexing) to index this drive in seconds instead of minutes.";
    public const string ServiceStoppedHint = "The NexPane Index Service is installed but not running; start it in Services to enable fast NTFS indexing.";
    public const string ServiceOutdatedHint = "Index service outdated: reinstall NexPane to update the NexPane Index Service.";

    public async Task<SourceDecision> SelectAsync(VolumeCandidate volume, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(volume);
        if (volume.IsNetwork)
        {
            return new SourceDecision(VolumeSourceKind.Crawler, VolumeKind.Smb, "Network share (scheduled crawl)");
        }

        var ntfs = volume.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        if (!ntfs || volume.IsRemovable)
        {
            var kind = volume.IsRemovable ? VolumeKind.Removable : VolumeKind.OtherLocal;
            return new SourceDecision(VolumeSourceKind.Walker, kind, $"Walker ({volume.FileSystem})");
        }

        var state = await service.ProbeAsync(ct).ConfigureAwait(false);
        if (state == IndexServiceState.Running)
        {
            return new SourceDecision(VolumeSourceKind.UsnService, VolumeKind.NtfsFixed, "NTFS fast index");
        }

        if (elevation.IsElevated)
        {
            return new SourceDecision(VolumeSourceKind.UsnInProc, VolumeKind.NtfsFixed, "NTFS fast index (in-process, elevated)");
        }

        var hint = state switch
        {
            IndexServiceState.NotInstalled => ServiceMissingHint,
            IndexServiceState.Stopped => ServiceStoppedHint,
            _ => ServiceOutdatedHint,
        };
        return new SourceDecision(VolumeSourceKind.Walker, VolumeKind.NtfsFixed, "Walker", hint);
    }
}
