using System.Globalization;
using Microsoft.Data.Sqlite;
using NexPane.Core.Indexing;
using NexPane.Search.Execution;
using NexPane.Storage;

namespace NexPane.Search.Tags;

public sealed record TagInfo(long TagId, string Name, string Color);

/// <summary>A tag assignment row of <c>file_tags</c>.</summary>
public sealed record TagAssignment(long TagId, string FileKey, string LastPath, long TaggedUtc);

/// <summary>
/// Tags (P3.27, §9.2): <c>tags</c> / <c>file_tags</c> in app.db. Files on NTFS volumes are keyed by identity
/// (<c>ntfs:&lt;volSerialHex&gt;:&lt;fileIdHex128&gt;</c>), so a tag survives renames and moves within the volume.
/// Other files are keyed by path (<c>path:&lt;folded full path&gt;</c>); those keys are repaired when the index reports a
/// rename or move of the file or of one of its ancestor folders. Implements <see cref="ITagLookup"/> for <c>tag:</c>.
/// </summary>
public sealed class TagService : ITagLookup, IDisposable
{
    private readonly SqliteConnectionFactory _appDb;
    private readonly SqliteWriter _writer;
    private readonly IIndexSnapshotSource _index;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    // Path-keyed assignments currently resolved to index entries (repair on rename).
    private Dictionary<EntryRef, List<PathTag>> _pathTagged = [];

    public TagService(SqliteConnectionFactory appDb, SqliteWriter writer, IIndexSnapshotSource index, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(appDb);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(index);
        _appDb = appDb;
        _writer = writer;
        _index = index;
        _clock = clock ?? TimeProvider.System;
        _index.DeltaApplied += OnDeltaApplied;
    }

    /// <summary>Completed path-key repairs (diagnostics/tests).</summary>
    public long Repairs { get; private set; }

    /// <summary>Loads path-keyed assignments and resolves them against the current index (call after startup scan).</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        var rows = await ReadAssignmentsAsync("SELECT tag_id, file_key, last_path, tagged_utc FROM file_tags WHERE file_key LIKE 'path:%';", null, ct).ConfigureAwait(false);
        var resolved = Resolve(_index.Current, rows, ct);
        lock (_gate)
        {
            _pathTagged = resolved;
        }
    }

    public async Task<TagInfo> CreateTagAsync(string name, string color = "#808080", CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var id = await _writer.ExecuteAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO tags(name, color) VALUES ($n, $c) ON CONFLICT(name) DO UPDATE SET color = excluded.color RETURNING tag_id;";
            cmd.Parameters.AddWithValue("$n", name.Trim());
            cmd.Parameters.AddWithValue("$c", color);
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }, ct).ConfigureAwait(false);
        return new TagInfo(id, name.Trim(), color);
    }

    public async Task<IReadOnlyList<TagInfo>> ListTagsAsync(CancellationToken ct)
    {
        var connection = await _appDb.OpenAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT tag_id, name, color FROM tags ORDER BY name COLLATE NOCASE;";
            var list = new List<TagInfo>();
            var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    list.Add(new TagInfo(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
                }
            }

            return list;
        }
    }

    public Task DeleteTagAsync(string name, CancellationToken ct) =>
        _writer.ExecuteAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM tags WHERE name = $n;";
            cmd.Parameters.AddWithValue("$n", name);
            return cmd.ExecuteNonQuery();
        }, ct);

    /// <summary>The file key of an index entry (§9.2).</summary>
    public static string FileKey(IIndexSnapshot snapshot, EntryRef entry)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var segment = snapshot.GetSegment(entry.Segment);
        var volume = segment.Volume;
        if (volume.HasStableFileIds)
        {
            return string.Create(CultureInfo.InvariantCulture, $"ntfs:{volume.VolumeSerial:X8}:{0UL:X16}{segment.GetFileId(entry.Index):X16}");
        }

        return PathKey(segment.GetFullPath(entry.Index));
    }

    public static string PathKey(string fullPath) => "path:" + NameArena.Fold(fullPath);

    /// <summary>Tags an indexed entry (creating the tag if needed).</summary>
    public async Task TagAsync(EntryRef entry, string tagName, CancellationToken ct)
    {
        var snapshot = _index.Current;
        var key = FileKey(snapshot, entry);
        var path = snapshot.GetFullPath(entry);
        var tag = await CreateTagIfMissingAsync(tagName, ct).ConfigureAwait(false);
        var now = _clock.GetUtcNow().ToFileTime();
        await _writer.ExecuteAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO file_tags(tag_id, file_key, last_path, tagged_utc) VALUES ($t, $k, $p, $u)
                ON CONFLICT(tag_id, file_key) DO UPDATE SET last_path = excluded.last_path;
                """;
            cmd.Parameters.AddWithValue("$t", tag.TagId);
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$u", now);
            return cmd.ExecuteNonQuery();
        }, ct).ConfigureAwait(false);

        if (key.StartsWith("path:", StringComparison.Ordinal))
        {
            lock (_gate)
            {
                if (!_pathTagged.TryGetValue(entry, out var list))
                {
                    list = [];
                    _pathTagged[entry] = list;
                }

                list.Add(new PathTag(tag.TagId, key));
            }
        }
    }

    public async Task UntagAsync(EntryRef entry, string tagName, CancellationToken ct)
    {
        var key = FileKey(_index.Current, entry);
        await _writer.ExecuteAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM file_tags WHERE file_key = $k AND tag_id = (SELECT tag_id FROM tags WHERE name = $n);";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$n", tagName);
            return cmd.ExecuteNonQuery();
        }, ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (_pathTagged.TryGetValue(entry, out var list))
            {
                list.RemoveAll(p => p.FileKey == key);
            }
        }
    }

    /// <summary>Tag names of an entry.</summary>
    public async Task<IReadOnlyList<string>> GetTagsAsync(EntryRef entry, CancellationToken ct)
    {
        var key = FileKey(_index.Current, entry);
        var connection = await _appDb.OpenAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT t.name FROM file_tags f JOIN tags t ON t.tag_id = f.tag_id WHERE f.file_key = $k ORDER BY t.name;";
            cmd.Parameters.AddWithValue("$k", key);
            var names = new List<string>();
            var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    names.Add(reader.GetString(0));
                }
            }

            return names;
        }
    }

    public async Task<IReadOnlyList<TagAssignment>> GetAssignmentsAsync(string tagName, CancellationToken ct) =>
        await ReadAssignmentsAsync(
            "SELECT f.tag_id, f.file_key, f.last_path, f.tagged_utc FROM file_tags f JOIN tags t ON t.tag_id = f.tag_id WHERE t.name = $n;",
            cmd => cmd.Parameters.AddWithValue("$n", tagName), ct).ConfigureAwait(false);

    /// <summary><c>tag:a;b</c> → entries carrying any of the tags.</summary>
    public async Task<EntryKeySet> ResolveAsync(IReadOnlyList<string> tagNames, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tagNames);
        var ids = new List<(ulong, ulong)>();
        var paths = new List<string>();
        foreach (var name in tagNames)
        {
            foreach (var row in await GetAssignmentsAsync(name, ct).ConfigureAwait(false))
            {
                if (TryParseNtfsKey(row.FileKey, out var serial, out var fileId))
                {
                    ids.Add((serial, fileId));
                }
                else
                {
                    paths.Add(row.LastPath);
                }
            }
        }

        return new EntryKeySet(ids, paths);
    }

    public void Dispose() => _index.DeltaApplied -= OnDeltaApplied;

    public static bool TryParseNtfsKey(string key, out ulong serial, out ulong fileId)
    {
        serial = 0;
        fileId = 0;
        var parts = key.Split(':');
        return parts.Length == 3 && parts[0] == "ntfs" && parts[2].Length == 32 &&
               ulong.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out serial) &&
               ulong.TryParse(parts[2].AsSpan(16), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out fileId);
    }

    private async Task<TagInfo> CreateTagIfMissingAsync(string name, CancellationToken ct)
    {
        var existing = (await ListTagsAsync(ct).ConfigureAwait(false)).FirstOrDefault(t => t.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        return existing ?? await CreateTagAsync(name, ct: ct).ConfigureAwait(false);
    }

    private async Task<List<TagAssignment>> ReadAssignmentsAsync(string sql, Action<SqliteCommand>? bind, CancellationToken ct)
    {
        var connection = await _appDb.OpenAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            bind?.Invoke(cmd);
            var list = new List<TagAssignment>();
            var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    list.Add(new TagAssignment(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
                }
            }

            return list;
        }
    }

    /// <summary>Resolves path-keyed rows to entries with one pass over the index (name pre-filter, then full path).</summary>
    private static Dictionary<EntryRef, List<PathTag>> Resolve(IIndexSnapshot snapshot, List<TagAssignment> rows, CancellationToken ct)
    {
        var result = new Dictionary<EntryRef, List<PathTag>>();
        if (rows.Count == 0)
        {
            return result;
        }

        var byKey = rows.GroupBy(r => r.FileKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var names = new HashSet<string>(rows.Select(r => NameArena.Fold(Path.GetFileName(r.LastPath.TrimEnd('\\', '/')))), StringComparer.Ordinal)
            .GetAlternateLookup<ReadOnlySpan<char>>();
        for (var s = 0; s < snapshot.SegmentCount; s++)
        {
            if (snapshot is IndexGeneration g && !g.HasSegment(s))
            {
                continue;
            }

            var segment = snapshot.GetSegment(s);
            for (var i = 1; i < snapshot.Count(s); i++)
            {
                if ((i & 0x3FF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                if (segment.IsDeleted(i) || !names.Contains(segment.GetFoldedName(i)))
                {
                    continue;
                }

                var key = PathKey(segment.GetFullPath(i));
                if (byKey.TryGetValue(key, out var tagged))
                {
                    result[new EntryRef(s, i)] = tagged.Select(t => new PathTag(t.TagId, t.FileKey)).ToList();
                }
            }
        }

        return result;
    }

    private void OnDeltaApplied(object? sender, IndexDeltaBatch batch)
    {
        if (batch.ResetSegments.Count > 0)
        {
            // Entry refs of those segments changed: re-resolve from the stored last paths in the background.
            _ = Task.Run(() => InitializeAsync(CancellationToken.None));
            return;
        }

        Dictionary<EntryRef, List<PathTag>> snapshotMap;
        lock (_gate)
        {
            if (_pathTagged.Count == 0)
            {
                return;
            }

            snapshotMap = new Dictionary<EntryRef, List<PathTag>>(_pathTagged);
        }

        var snapshot = _index.Current;
        var touched = new List<(EntryRef Entry, PathTag Tag, string NewKey, string NewPath)>();
        foreach (var change in batch.Changes)
        {
            if (change.Kind is not (IndexChangeKind.Renamed or IndexChangeKind.Removed))
            {
                continue;
            }

            foreach (var (entry, tags) in snapshotMap)
            {
                if (entry.Segment != change.Entry.Segment || !IsSelfOrDescendant(snapshot, entry, change.Entry.Index))
                {
                    continue;
                }

                if (change.Kind == IndexChangeKind.Removed)
                {
                    continue; // keep the assignment; it re-attaches if the path reappears (restore from recycle bin)
                }

                var newPath = snapshot.GetFullPath(entry);
                var newKey = PathKey(newPath);
                foreach (var tag in tags.Where(t => t.FileKey != newKey))
                {
                    touched.Add((entry, tag, newKey, newPath));
                }
            }
        }

        if (touched.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var (entry, tag, newKey, _) in touched)
            {
                if (_pathTagged.TryGetValue(entry, out var list))
                {
                    var i = list.IndexOf(tag);
                    if (i >= 0)
                    {
                        list[i] = tag with { FileKey = newKey };
                    }
                }
            }
        }

        _ = _writer.ExecuteInTransactionAsync((c, t) =>
        {
            foreach (var (_, tag, newKey, newPath) in touched)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = t;
                cmd.CommandText = "UPDATE OR REPLACE file_tags SET file_key = $new, last_path = $path WHERE tag_id = $t AND file_key = $old;";
                cmd.Parameters.AddWithValue("$new", newKey);
                cmd.Parameters.AddWithValue("$path", newPath);
                cmd.Parameters.AddWithValue("$t", tag.TagId);
                cmd.Parameters.AddWithValue("$old", tag.FileKey);
                cmd.ExecuteNonQuery();
            }

            Repairs += touched.Count;
            return touched.Count;
        });
    }

    private static bool IsSelfOrDescendant(IIndexSnapshot snapshot, EntryRef entry, int ancestor)
    {
        var segment = snapshot.GetSegment(entry.Segment);
        var current = entry.Index;
        for (var guard = 0; guard < 4096; guard++)
        {
            if (current == ancestor)
            {
                return true;
            }

            if (current == 0)
            {
                return false;
            }

            current = segment.GetParent(current);
        }

        return false;
    }

    private sealed record PathTag(long TagId, string FileKey);
}
