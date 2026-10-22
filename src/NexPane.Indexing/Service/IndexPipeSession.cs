namespace NexPane.Indexing.Service;

/// <summary>What the Index Service serves (implemented by <see cref="Ntfs.NtfsVolumeBackend"/>; tests use their own).</summary>
public interface IIndexVolumeBackend
{
    IReadOnlyList<VolumeInfoMessage> ListVolumes();

    /// <summary>Streams the volume's entries the client may see (batches) and returns the USN to subscribe from.</summary>
    Task<long> EnumerateAsync(string volumeGuid, Func<string, bool> canList, Func<IReadOnlyList<WireEntry>, Task> sink, CancellationToken ct);

    /// <summary>Streams journal changes from <paramref name="fromUsn"/> until cancelled.</summary>
    Task SubscribeAsync(string volumeGuid, long fromUsn, Func<string, bool> canList, Func<IReadOnlyList<WireChange>, Task> sink, CancellationToken ct);
}

/// <summary>
/// Server side of one client connection (§7.5): <c>Hello</c> version handshake (mismatch → <c>Error</c> and close),
/// then <c>ListVolumes</c>, <c>Enumerate</c> and <c>Subscribe</c> requests. Every streamed entry passed the client's
/// <c>canList</c> privacy check in the backend.
/// </summary>
public static class IndexPipeSession
{
    public const int ErrorVersionMismatch = 1;
    public const int ErrorUnknownVolume = 2;
    public const int ErrorBackend = 3;
    public const int ErrorProtocol = 4;

    public static async Task RunAsync(Stream stream, IIndexVolumeBackend backend, Func<Func<string, bool>> accessFactory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(accessFactory);
        try
        {
            var hello = await IndexProtocol.ReadAsync(stream, ct).ConfigureAwait(false);
            if (hello?.Message is not HelloMessage { Version: IndexProtocol.Version })
            {
                await IndexProtocol.WriteAsync(stream, new ErrorMessage(ErrorVersionMismatch,
                    $"Index service protocol {IndexProtocol.Version} expected."), ct).ConfigureAwait(false);
                return;
            }

            // A pipe client can only be impersonated after data was read from it, so the access check is created now.
            var canList = accessFactory();
            await IndexProtocol.WriteAsync(stream, new HelloMessage(IndexProtocol.Version), ct).ConfigureAwait(false);
            while (await IndexProtocol.ReadAsync(stream, ct).ConfigureAwait(false) is { } frame)
            {
                switch (frame.Message)
                {
                    case ListVolumesMessage:
                    {
                        var volumes = backend.ListVolumes();
                        for (var i = 0; i < volumes.Count; i++)
                        {
                            await IndexProtocol.WriteAsync(stream, volumes[i], ct, i == volumes.Count - 1 ? FrameOptions.Final : FrameOptions.None).ConfigureAwait(false);
                        }

                        if (volumes.Count == 0)
                        {
                            await IndexProtocol.WriteAsync(stream, new EnumerateDoneMessage(0), ct, FrameOptions.Final).ConfigureAwait(false);
                        }

                        break;
                    }

                    case EnumerateMessage enumerate when Known(backend, enumerate.VolumeGuid):
                    {
                        var next = await backend.EnumerateAsync(enumerate.VolumeGuid, canList,
                            batch => IndexProtocol.WriteAsync(stream, new EntryBatchMessage(batch), ct), ct).ConfigureAwait(false);
                        await IndexProtocol.WriteAsync(stream, new EnumerateDoneMessage(next), ct).ConfigureAwait(false);
                        break;
                    }

                    case SubscribeMessage subscribe when Known(backend, subscribe.VolumeGuid):
                        await backend.SubscribeAsync(subscribe.VolumeGuid, subscribe.FromUsn, canList,
                            batch => IndexProtocol.WriteAsync(stream, new ChangeBatchMessage(batch), ct), ct).ConfigureAwait(false);
                        return;

                    case EnumerateMessage or SubscribeMessage:
                        await IndexProtocol.WriteAsync(stream, new ErrorMessage(ErrorUnknownVolume, "Unknown volume."), ct).ConfigureAwait(false);
                        break;

                    default:
                        await IndexProtocol.WriteAsync(stream, new ErrorMessage(ErrorProtocol, $"Unexpected {frame.Message.Type} request."), ct).ConfigureAwait(false);
                        return;
                }
            }
        }
        catch (ProtocolException ex)
        {
            await TryWriteErrorAsync(stream, ErrorProtocol, ex.Message).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Client disconnected.
            return;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidDataException)
        {
            await TryWriteErrorAsync(stream, ErrorBackend, ex.Message).ConfigureAwait(false);
        }
    }

    private static bool Known(IIndexVolumeBackend backend, string guid) =>
        backend.ListVolumes().Any(v => string.Equals(v.VolumeGuid, guid, StringComparison.OrdinalIgnoreCase));

    private static async Task TryWriteErrorAsync(Stream stream, int code, string message)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await IndexProtocol.WriteAsync(stream, new ErrorMessage(code, message), timeout.Token).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return; // the client is already gone
        }
        catch (OperationCanceledException)
        {
            return;
        }
    }
}
