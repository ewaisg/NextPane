using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.Win32;
using NexPane.Core.Indexing;
using NexPane.Indexing.Sources;
using NexPane.Indexing.Store;
using NexPane.Shell.Security;

namespace NexPane.Indexing.Service;

/// <summary>
/// Client of the NexPane Index Service pipe <c>\.\pipe\NexPane.Index.v1</c> (§7.5, P3.13): probes availability,
/// lists volumes, streams a volume's entries into the <see cref="IndexStore"/> and follows its journal.
/// The client connects with <c>SECURITY_IDENTIFICATION</c> (the server can check the user's access but never act as the
/// user) and only talks to a verified server: by default the pipe's server process must be the running
/// <paramref name="serviceName"/> service (process id from the Service Control Manager) configured as LocalSystem with
/// the <c>NexPane.IndexService.exe</c> image, so a process that squats the pipe name is rejected.
/// </summary>
/// <param name="verifyServer">Overrides the server check (tests that host the server in-process); receives the server's process id.</param>
public sealed class IndexServiceClient(string pipeName = IndexProtocol.PipeName, TimeSpan? connectTimeout = null, string serviceName = IndexServiceClient.ServiceName,
    Func<int, bool>? verifyServer = null) : IIndexServiceProbe
{
    public const string ServiceName = "NexPaneIndex";
    public const string ServiceImage = "NexPane.IndexService.exe";
    private readonly TimeSpan _connectTimeout = connectTimeout ?? TimeSpan.FromMilliseconds(500);
    private readonly Func<int, bool> _verifyServer = verifyServer ?? (pid => IsInstalledService(serviceName, pid));

    /// <summary>True when <paramref name="processId"/> is the running <paramref name="service"/> (LocalSystem, NexPane image).</summary>
    public static bool IsInstalledService(string service, int processId)
    {
        var info = ServerIdentity.TryQueryService(service);
        return info is { Running: true, RunsAsLocalSystem: true } && info.ProcessId == processId && processId != 0 &&
               string.Equals(info.ImageFileName, ServiceImage, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IndexServiceState> ProbeAsync(CancellationToken ct)
    {
        try
        {
            var connection = await ConnectAsync(ct).ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            return IndexServiceState.Running;
        }
        catch (TimeoutException)
        {
            return ServiceInstalled() ? IndexServiceState.Stopped : IndexServiceState.NotInstalled;
        }
        catch (ProtocolException)
        {
            return IndexServiceState.Outdated;
        }
        catch (UntrustedIndexServerException)
        {
            // Someone else serves the pipe name: the real service is not running (or not installed).
            return ServiceInstalled() ? IndexServiceState.Stopped : IndexServiceState.NotInstalled;
        }
        catch (IOException)
        {
            return ServiceInstalled() ? IndexServiceState.Stopped : IndexServiceState.NotInstalled;
        }
    }

    /// <summary>Connects and performs the version handshake.</summary>
    /// <exception cref="ProtocolException">The service speaks another protocol version ("Index service outdated").</exception>
    public async Task<Connection> ConnectAsync(CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(_connectTimeout, ct).ConfigureAwait(false);
            if (!ServerIdentity.TryGetPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || !_verifyServer(serverPid))
            {
                throw new UntrustedIndexServerException($"The pipe {pipeName} is served by process {serverPid}, which is not the {serviceName} service.");
            }

            await IndexProtocol.WriteAsync(pipe, new HelloMessage(IndexProtocol.Version), ct).ConfigureAwait(false);
            var reply = await IndexProtocol.ReadAsync(pipe, ct).ConfigureAwait(false);
            if (reply?.Message is not HelloMessage { Version: IndexProtocol.Version })
            {
                throw new ProtocolException(reply?.Message is ErrorMessage e ? e.Message : "Index service outdated.");
            }

            return new Connection(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private bool ServiceInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + serviceName);
        return key is not null;
    }

    /// <summary>One handshaken connection.</summary>
    public sealed class Connection(Stream stream) : IAsyncDisposable
    {
        public async Task<IReadOnlyList<VolumeInfoMessage>> ListVolumesAsync(CancellationToken ct)
        {
            await IndexProtocol.WriteAsync(stream, new ListVolumesMessage(), ct).ConfigureAwait(false);
            var volumes = new List<VolumeInfoMessage>();
            while (await IndexProtocol.ReadAsync(stream, ct).ConfigureAwait(false) is { } frame)
            {
                switch (frame.Message)
                {
                    case VolumeInfoMessage v:
                        volumes.Add(v);
                        break;
                    case ErrorMessage e:
                        throw ToException(e);
                }

                if ((frame.Flags & FrameOptions.Final) != 0)
                {
                    break;
                }
            }

            return volumes;
        }

        /// <summary>Streams the volume's entries into <paramref name="store"/>; returns the USN to subscribe from.</summary>
        public async Task<long> EnumerateIntoAsync(string volumeGuid, IndexStore store, SegmentHandle segment, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(segment);
            await IndexProtocol.WriteAsync(stream, new EnumerateMessage(volumeGuid), ct).ConfigureAwait(false);
            while (await IndexProtocol.ReadAsync(stream, ct).ConfigureAwait(false) is { } frame)
            {
                switch (frame.Message)
                {
                    case EntryBatchMessage batch:
                        await store.SubmitAsync(segment.Id, batch.Entries.Select(e =>
                            IndexDelta.UpsertById(e.FileReference, e.ParentReference, e.Name, e.Attributes, e.Size, e.ModifiedUtc, e.CreatedUtc)).ToList(), ct).ConfigureAwait(false);
                        break;
                    case EnumerateDoneMessage done:
                        await store.FlushAsync(ct).ConfigureAwait(false);
                        return done.NextUsn;
                    case ErrorMessage e:
                        throw ToException(e);
                }
            }

            throw new IOException("The index service closed the connection during enumeration.");
        }

        /// <summary>Applies journal changes to <paramref name="store"/> until cancelled; <paramref name="onUsn"/> receives
        /// the last applied USN (persisted for resume).</summary>
        public async Task FollowAsync(string volumeGuid, long fromUsn, IndexStore store, SegmentHandle segment, Action<long>? onUsn, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(segment);
            await IndexProtocol.WriteAsync(stream, new SubscribeMessage(volumeGuid, fromUsn), ct).ConfigureAwait(false);
            while (await IndexProtocol.ReadAsync(stream, ct).ConfigureAwait(false) is { } frame)
            {
                switch (frame.Message)
                {
                    case ChangeBatchMessage batch:
                    {
                        var records = batch.Changes.Select(c => new Ntfs.UsnRecord(3, 0, c.FileReference, c.ParentReference, c.Usn, 0,
                            (Ntfs.UsnReason)c.Reason, 0, 0, c.Attributes, c.Name, [], 0)).ToList();
                        var stats = await StatAsync(store, segment, batch.Changes, ct).ConfigureAwait(false);
                        var deltas = Ntfs.UsnChangeTranslator.Translate(records, (ulong frn, out uint attributes, out long size, out long created, out long modified) =>
                        {
                            var found = stats.TryGetValue(frn, out var s);
                            (attributes, size, created, modified) = s;
                            return found;
                        });
                        await store.SubmitAsync(segment.Id, deltas, ct).ConfigureAwait(false);
                        if (batch.Changes.Count > 0)
                        {
                            onUsn?.Invoke(batch.Changes[^1].Usn);
                        }

                        break;
                    }

                    case ErrorMessage e:
                        throw ToException(e);
                }
            }
        }

        public ValueTask DisposeAsync() => stream.DisposeAsync();

        /// <summary>Maps a service error to an exception by its code (<see cref="IndexPipeSession.ErrorJournalReset"/> → <see cref="JournalResetException"/>).</summary>
        private static IOException ToException(ErrorMessage error) => error.Code == IndexPipeSession.ErrorJournalReset
            ? new JournalResetException(error.Message)
            : new IOException($"Index service error {error.Code}: {error.Message}");

        /// <summary>The service sends no sizes with changes: stat each file by its path (parent path from the index +
        /// name), which works unelevated.</summary>
        private static async Task<Dictionary<ulong, (uint Attributes, long Size, long Created, long Modified)>> StatAsync(
            IndexStore store, SegmentHandle segment, IReadOnlyList<WireChange> changes, CancellationToken ct)
        {
            var parents = await store.ResolvePathsAsync(segment.Id, changes.Select(c => c.ParentReference).Distinct().ToList(), ct).ConfigureAwait(false);
            var result = new Dictionary<ulong, (uint, long, long, long)>();
            foreach (var change in changes)
            {
                if (!parents.TryGetValue(change.ParentReference, out var parent) || parent is null)
                {
                    continue;
                }

                try
                {
                    var path = Path.Combine(parent, change.Name);
                    FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
                    if (info.Exists)
                    {
                        result[change.FileReference] = ((uint)info.Attributes, info is FileInfo f ? f.Length : 0,
                            info.CreationTimeUtc.ToFileTimeUtc(), info.LastWriteTimeUtc.ToFileTimeUtc());
                    }
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
            }

            return result;
        }
    }
}
