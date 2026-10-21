using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexPane.Storage.Settings;

/// <summary>Raised after settings changed (in-process update or external edit of the file).</summary>
public sealed class SettingsChangedEventArgs(IReadOnlyCollection<string> sections, bool external) : EventArgs
{
    /// <summary>Top-level section names whose content changed.</summary>
    public IReadOnlyCollection<string> Sections { get; } = sections;

    /// <summary>True when the file was edited outside this process.</summary>
    public bool External { get; } = external;
}

/// <summary>
/// <c>%APPDATA%\NexPane\settings.json</c> (§9): human-editable, schema-versioned JSON document written atomically
/// (temp file + flush + <see cref="File.Replace(string, string, string?)"/>). Writers are serialized; a corrupt file is
/// moved aside to <c>settings.json.corrupt-&lt;timestamp&gt;</c> and defaults are used. Unknown sections are preserved.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaVersionProperty = "schemaVersion";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Lock _stateGate = new();
    private readonly TimeProvider _clock;
    private JsonObject _root;
    private string _lastWrittenHash = string.Empty;
    private FileSystemWatcher? _watcher;

    private SettingsStore(string filePath, TimeProvider clock, JsonObject root)
    {
        FilePath = filePath;
        _clock = clock;
        _root = root;
    }

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public string FilePath { get; }

    /// <summary>Path of the corrupt-file copy made during <see cref="Load"/>, if any.</summary>
    public string? RecoveredCorruptCopy { get; private set; }

    public int SchemaVersion
    {
        get
        {
            lock (_stateGate)
            {
                return _root[SchemaVersionProperty]?.GetValue<int>() ?? CurrentSchemaVersion;
            }
        }
    }

    /// <summary>Loads the settings file (or defaults). Leftover temp files from an interrupted write are removed.</summary>
    public static SettingsStore Load(string filePath, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        clock ??= TimeProvider.System;
        filePath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(directory);
        CleanupTempFiles(filePath);

        string? corruptCopy = null;
        JsonObject root;
        if (File.Exists(filePath))
        {
            var text = File.ReadAllText(filePath, Encoding.UTF8);
            if (TryParseDocument(text, out var parsed))
            {
                root = Upgrade(parsed);
            }
            else
            {
                corruptCopy = filePath + ".corrupt-" + clock.GetUtcNow().ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
                File.Copy(filePath, corruptCopy, overwrite: true);
                root = SettingsDefaults.Create();
            }
        }
        else
        {
            root = SettingsDefaults.Create();
        }

        var store = new SettingsStore(filePath, clock, root) { RecoveredCorruptCopy = corruptCopy };
        if (corruptCopy is not null || !File.Exists(filePath))
        {
            store.WriteFileAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        return store;
    }

    /// <summary>Returns a deep copy of a top-level section, or null.</summary>
    public JsonNode? GetSection(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_stateGate)
        {
            return _root[name]?.DeepClone();
        }
    }

    /// <summary>Deserializes a section into <typeparamref name="T"/>, or returns <paramref name="fallback"/>.</summary>
    public T Get<T>(string section, T fallback)
    {
        var node = GetSection(section);
        if (node is null)
        {
            return fallback;
        }

        try
        {
            return node.Deserialize<T>() ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    /// <summary>Replaces a section and persists the file.</summary>
    public Task SetSectionAsync(string name, JsonNode? value, CancellationToken ct) =>
        UpdateAsync(root => root[name] = value?.DeepClone(), ct);

    /// <summary>Serializes <paramref name="value"/> into a section and persists the file.</summary>
    public Task SetAsync<T>(string section, T value, CancellationToken ct) =>
        SetSectionAsync(section, JsonSerializer.SerializeToNode(value), ct);

    /// <summary>Applies a mutation to a copy of the document and persists it atomically. Mutations are serialized.</summary>
    public async Task UpdateAsync(Action<JsonObject> mutate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        List<string> changed;
        try
        {
            JsonObject before;
            JsonObject after;
            lock (_stateGate)
            {
                before = (JsonObject)_root.DeepClone();
            }

            after = (JsonObject)before.DeepClone();
            mutate(after);
            after[SchemaVersionProperty] = CurrentSchemaVersion;
            changed = DiffSections(before, after);
            lock (_stateGate)
            {
                _root = after;
            }

            await WriteFileCoreAsync(after, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        if (changed.Count > 0)
        {
            Changed?.Invoke(this, new SettingsChangedEventArgs(changed, external: false));
        }
    }

    /// <summary>Watches the file for edits made outside this process and reloads them.</summary>
    public void StartWatching()
    {
        if (_watcher is not null)
        {
            return;
        }

        _watcher = new FileSystemWatcher(Path.GetDirectoryName(FilePath)!, Path.GetFileName(FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _watcher.Changed += (_, _) => ReloadFromDisk();
        _watcher.Renamed += (_, _) => ReloadFromDisk();
        _watcher.Created += (_, _) => ReloadFromDisk();
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Re-reads the file; when it changed externally and parses, the new content is adopted and
    /// <see cref="Changed"/> is raised. Returns true when a reload happened.</summary>
    public bool ReloadFromDisk()
    {
        string text;
        try
        {
            text = File.ReadAllText(FilePath, Encoding.UTF8);
        }
        catch (IOException)
        {
            return false; // being replaced right now; the next notification re-reads it
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        var hash = Hash(text);
        if (!_writeGate.Wait(0))
        {
            return false; // an in-process write is running; it will publish its own content
        }

        List<string> changed;
        try
        {
            if (hash == _lastWrittenHash || !TryParseDocument(text, out var parsed))
            {
                return false;
            }

            var upgraded = Upgrade(parsed);
            lock (_stateGate)
            {
                changed = DiffSections(_root, upgraded);
                _root = upgraded;
            }

            _lastWrittenHash = hash;
        }
        finally
        {
            _writeGate.Release();
        }

        if (changed.Count > 0)
        {
            Changed?.Invoke(this, new SettingsChangedEventArgs(changed, external: true));
        }

        return changed.Count > 0;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _writeGate.Dispose();
    }

    private async Task WriteFileAsync(CancellationToken ct)
    {
        JsonObject copy;
        lock (_stateGate)
        {
            copy = (JsonObject)_root.DeepClone();
        }

        await WriteFileCoreAsync(copy, ct).ConfigureAwait(false);
    }

    private async Task WriteFileCoreAsync(JsonObject document, CancellationToken ct)
    {
        var text = document.ToJsonString(WriteOptions);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        var temp = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            _lastWrittenHash = Hash(text);
            if (File.Exists(FilePath))
            {
                File.Replace(temp, FilePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, FilePath);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static bool TryParseDocument(string text, out JsonObject document)
    {
        document = null!;
        try
        {
            var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (node is not JsonObject obj)
            {
                return false;
            }

            var version = obj[SchemaVersionProperty];
            if (version is null || version.GetValueKind() != JsonValueKind.Number || !version.AsValue().TryGetValue<int>(out var v) || v < 1)
            {
                return false;
            }

            document = obj;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Upgrades older schema versions and fills sections missing from the file with defaults.</summary>
    private static JsonObject Upgrade(JsonObject document)
    {
        var defaults = SettingsDefaults.Create();
        foreach (var (name, value) in defaults)
        {
            if (!document.ContainsKey(name))
            {
                document[name] = value?.DeepClone();
            }
        }

        return document;
    }

    private static List<string> DiffSections(JsonObject before, JsonObject after)
    {
        var names = new HashSet<string>(before.Select(p => p.Key), StringComparer.Ordinal);
        names.UnionWith(after.Select(p => p.Key));
        var changed = new List<string>();
        foreach (var name in names)
        {
            if (!JsonNode.DeepEquals(before[name], after[name]))
            {
                changed.Add(name);
            }
        }

        return changed;
    }

    private static void CleanupTempFiles(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath)!;
        foreach (var temp in Directory.EnumerateFiles(directory, Path.GetFileName(filePath) + ".tmp-*"))
        {
            File.Delete(temp);
        }
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
