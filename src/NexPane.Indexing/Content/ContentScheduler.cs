using NexPane.Core.Indexing;
using NexPane.Shell;
using NexPane.Shell.FileSystem;

namespace NexPane.Indexing.Content;

/// <summary>Content pipeline limits (§7.6; edited in Settings, P4.41).</summary>
public sealed record ContentOptions
{
    public long MaxFileBytes { get; init; } = 50L * 1024 * 1024;

    public int MaxChars { get; init; } = 2 * 1024 * 1024;

    public int Workers { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);

    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(30);

    public int MaxAttempts { get; init; } = 5;
}

/// <summary>Decides whether background extraction should pause right now.</summary>
public interface IContentPausePolicy
{
    /// <summary>Returns a reason to pause, or null to run.</summary>
    string? PauseReason();
}

/// <summary>Pauses on battery and when foreground CPU use exceeds a threshold (defaults: on, 80 %).</summary>
public sealed class SystemLoadPausePolicy(bool pauseOnBattery = true, int cpuPercent = 80) : IContentPausePolicy
{
    private readonly SystemLoad _load = new();

    public string? PauseReason()
    {
        if (pauseOnBattery && SystemLoad.IsOnBattery)
        {
            return "Paused on battery power";
        }

        var cpu = _load.SampleCpuPercent();
        return cpuPercent > 0 && cpu > cpuPercent ? $"Paused while CPU use is high ({cpu:F0} %)" : null;
    }
}

/// <summary>
/// Content scheduler (§7.6, P3.30): claims <c>work_queue</c> items by priority, extracts text with the first matching
/// <see cref="IContentExtractor"/>, writes <c>documents</c>/<c>doc_text</c>, retries failures with exponential backoff
/// (up to 5 attempts) and pauses on demand or by policy (battery, CPU). Workers are dedicated below-normal threads.
/// </summary>
public sealed class ContentScheduler : IAsyncDisposable
{
    private static readonly ContentWorkKind[] HandledKinds = [ContentWorkKind.Extract];
    private readonly ContentStore _store;
    private readonly IReadOnlyList<IContentExtractor> _extractors;
    private readonly ContentOptions _options;
    private readonly IContentPausePolicy? _policy;
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Thread> _threads = [];
    private readonly Dictionary<string, (string Root, VolumeKind Kind, ulong Serial)> _volumes = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _manualPause;
    private long _processed;
    private long _failed;

    public ContentScheduler(ContentStore store, IReadOnlyList<IContentExtractor> extractors, ContentOptions? options = null, IContentPausePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(extractors);
        _store = store;
        _extractors = extractors;
        _options = options ?? new ContentOptions();
        _policy = policy;
    }

    public long Processed => Interlocked.Read(ref _processed);

    public long Failed => Interlocked.Read(ref _failed);

    public bool IsPaused => PauseReason is not null;

    /// <summary>Why work is paused (manual pause or policy), or null.</summary>
    public string? PauseReason => _manualPause ? "Paused by the user" : _policy?.PauseReason();

    public void Pause() => _manualPause = true;

    public void Resume()
    {
        _manualPause = false;
        Signal();
    }

    /// <summary>Wakes idle workers (call after enqueueing).</summary>
    public void Signal() => _signal.Release();

    public void Start()
    {
        for (var i = 0; i < _options.Workers; i++)
        {
            var thread = new Thread(WorkerLoop) { IsBackground = true, Name = "NexPane content " + i, Priority = ThreadPriority.BelowNormal };
            _threads.Add(thread);
            thread.Start();
        }
    }

    /// <summary>Processes the currently ready items once on the calling thread. Returns the number processed
    /// (0 when paused).</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var processed = 0;
        while (!IsPaused)
        {
            var items = await _store.ClaimAsync(HandledKinds, 16, ct).ConfigureAwait(false);
            if (items.Count == 0)
            {
                break;
            }

            foreach (var item in items)
            {
                await ProcessAsync(item, ct).ConfigureAwait(false);
                processed++;
            }
        }

        return processed;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _signal.Release(_threads.Count + 1);
        foreach (var thread in _threads)
        {
            thread.Join(TimeSpan.FromSeconds(10));
        }

        _stop.Dispose();
        _signal.Dispose();
    }

    private void WorkerLoop()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (IsPaused)
                {
                    _signal.Wait(TimeSpan.FromSeconds(1), ct);
                    continue;
                }

                var items = _store.ClaimAsync(HandledKinds, 1, ct).GetAwaiter().GetResult();
                if (items.Count == 0)
                {
                    _signal.Wait(TimeSpan.FromSeconds(1), ct);
                    continue;
                }

                ProcessAsync(items[0], ct).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ProcessAsync(WorkItem item, CancellationToken ct)
    {
        try
        {
            await ExtractAsync(item.Path, ct).ConfigureAwait(false);
            await _store.CompleteAsync(item, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _processed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Interlocked.Increment(ref _failed);
            var retry = await _store.FailAsync(item, ex.GetType().Name + ": " + ex.Message, _options.RetryBaseDelay, _options.MaxAttempts, ct).ConfigureAwait(false);
            if (!retry)
            {
                await RecordFailureAsync(item.Path, ex.Message, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Extracts one file (skipped when size and mtime are unchanged since the last successful extraction).</summary>
    public async Task ExtractAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            await _store.RemoveDocumentAsync(path, ct).ConfigureAwait(false);
            return;
        }

        var modified = info.LastWriteTimeUtc.ToFileTimeUtc();
        var stamp = await _store.GetDocumentStampAsync(path, ct).ConfigureAwait(false);
        if (stamp is { } s && s.Size == info.Length && s.ModifiedUtc == modified && s.Status is ExtractStatus.Ok or ExtractStatus.Unsupported or ExtractStatus.TooLarge)
        {
            return;
        }

        var extension = info.Extension.TrimStart('.');
        ExtractionResult result;
        if (info.Length > _options.MaxFileBytes)
        {
            result = new ExtractionResult(ExtractStatus.TooLarge, null, "none");
        }
        else
        {
            var extractor = _extractors.FirstOrDefault(e => e.CanExtract(extension));
            result = extractor is null ? ExtractionResult.Unsupported("none") : await extractor.ExtractAsync(path, _options.MaxChars, ct).ConfigureAwait(false);
            if (result.Status == ExtractStatus.Unsupported && extractor is not PlainTextExtractor && info.Length <= 4 * 1024 * 1024)
            {
                // Unknown type without a usable IFilter: accept it as text when it does not look binary.
                var text = PlainTextExtractor.Decode(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));
                if (text is not null)
                {
                    result = new ExtractionResult(ExtractStatus.Ok, text.Length > _options.MaxChars ? text[.._options.MaxChars] : text, "text");
                }
            }
        }

        var (root, kind, serial) = VolumeOf(path);
        FileIdentity.TryGet(path, out var identity);
        await _store.SaveDocumentAsync(new DocumentRecord(path, root, kind, serial, identity.FileIdLow, identity.FileIdHigh, info.Length, modified,
            result.Extractor, result.Status, result.Text, result.Error), ct).ConfigureAwait(false);
        if (result.NeedsOcr)
        {
            await _store.EnqueueAsync(path, ContentWorkKind.Ocr, 6, ct).ConfigureAwait(false);
        }
    }

    private async Task RecordFailureAsync(string path, string error, CancellationToken ct)
    {
        var info = new FileInfo(path);
        var (root, kind, serial) = VolumeOf(path);
        FileIdentity.TryGet(path, out var identity);
        await _store.SaveDocumentAsync(new DocumentRecord(path, root, kind, serial, identity.FileIdLow, identity.FileIdHigh,
            info.Exists ? info.Length : 0, info.Exists ? info.LastWriteTimeUtc.ToFileTimeUtc() : 0, "none", ExtractStatus.Failed, null, error), ct).ConfigureAwait(false);
    }

    private (string Root, VolumeKind Kind, ulong Serial) VolumeOf(string path)
    {
        var key = Path.GetPathRoot(path) ?? path;
        lock (_volumes)
        {
            if (_volumes.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var facts = VolumeFacts.TryGet(path);
        var value = facts is null
            ? (key, VolumeKind.OtherLocal, 0UL)
            : (facts.RootPath, facts.IsNetwork ? VolumeKind.Smb : facts.IsNtfs && facts.DriveType == DriveType.Fixed ? VolumeKind.NtfsFixed
                : facts.DriveType == DriveType.Removable ? VolumeKind.Removable : VolumeKind.OtherLocal, (ulong)facts.Serial);
        lock (_volumes)
        {
            _volumes[key] = value;
        }

        return value;
    }
}
