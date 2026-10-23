using System.Globalization;
using Microsoft.Data.Sqlite;
using NexPane.Core.Indexing;
using NexPane.Storage;

namespace NexPane.Indexing.Store;

/// <summary>Persisted per-volume state from the <c>volumes</c> table (§9.1).</summary>
public sealed record PersistedVolume(
    long VolumeId,
    IndexVolumeInfo Info,
    ulong RootFileId,
    VolumeSourceKind Source,
    VolumeScanState ScanState,
    long? UsnJournalId,
    long? LastUsn,
    long EntryCount,
    string? ConnectionId);

/// <summary>
/// Snapshot of the in-memory index in <c>index.db</c> (P3.08): changed rows are captured from the writer thread and
/// written by the database writer thread every 5 s; at startup the snapshot is loaded back so search works before the
/// volume sources finish their catch-up.
/// </summary>
internal sealed class IndexSnapshotPersistence : IAsyncDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    private readonly IndexStore _store;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriter _writer;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public IndexSnapshotPersistence(IndexStore store, SqliteConnectionFactory factory, SqliteWriter writer, TimeSpan? interval = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(writer);
        _store = store;
        _factory = factory;
        _writer = writer;
        _interval = interval ?? DefaultInterval;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Number of completed flushes (diagnostics).</summary>
    public long FlushCount { get; private set; }

    /// <summary>Last background flush error, if any (the next tick retries with a full write).</summary>
    public Exception? LastError { get; private set; }

    /// <summary>Starts the periodic writer (every 5 s by default).</summary>
    public void Start()
    {
        _loop ??= Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>Captures and writes all changes now.</summary>
    public async Task FlushAsync(bool full, CancellationToken ct)
    {
        var batches = await _store.CapturePersistBatchesAsync(full, ct).ConfigureAwait(false);
        if (batches.Count == 0)
        {
            return;
        }

        await _writer.ExecuteInTransactionAsync((connection, transaction) =>
        {
            foreach (var batch in batches)
            {
                Write(connection, transaction, batch);
            }

            return true;
        }, ct).ConfigureAwait(false);
        FlushCount++;
    }

    /// <summary>Updates journal/source/state columns of a volume row.</summary>
    public Task UpdateVolumeStateAsync(string root, VolumeSourceKind source, VolumeScanState state, long? journalId, long? lastUsn, string? error, CancellationToken ct) =>
        _writer.ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE volumes SET source = $source, scan_state = $state, usn_journal_id = COALESCE($journal, usn_journal_id),
                       last_usn = COALESCE($usn, last_usn), last_error = $error,
                       last_full_scan_utc = CASE WHEN $state = 2 THEN $now ELSE last_full_scan_utc END
                WHERE root = $root;
                """;
            command.Parameters.AddWithValue("$source", (int)source);
            command.Parameters.AddWithValue("$state", (int)state);
            command.Parameters.AddWithValue("$journal", (object?)journalId ?? DBNull.Value);
            command.Parameters.AddWithValue("$usn", (object?)lastUsn ?? DBNull.Value);
            command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", _clock.GetUtcNow().ToFileTime());
            command.Parameters.AddWithValue("$root", root);
            return command.ExecuteNonQuery();
        }, ct);

    /// <summary>Reads the persisted volume rows.</summary>
    public async Task<IReadOnlyList<PersistedVolume>> ReadVolumesAsync(CancellationToken ct)
    {
        var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT v.volume_id, v.kind, v.root, v.volume_guid, v.volume_serial, v.fs_name, v.connection_id, v.usn_journal_id,
                       v.last_usn, v.source, v.scan_state, v.entry_count,
                       (SELECT e.file_id FROM entries e WHERE e.volume_id = v.volume_id AND e.parent_id = e.file_id LIMIT 1)
                FROM volumes v ORDER BY v.volume_id;
                """;
            var list = new List<PersistedVolume>();
            var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var root = reader.GetString(2);
                    var kind = (VolumeKind)reader.GetInt32(1);
                    var info = new IndexVolumeInfo(
                        root,
                        kind,
                        kind is VolumeKind.Ftp or VolumeKind.Ftps or VolumeKind.Sftp or VolumeKind.WebDav ? '/' : '\\',
                        reader.IsDBNull(4) ? 0 : unchecked((ulong)reader.GetInt64(4)),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.IsDBNull(3) ? null : reader.GetString(3));
                    list.Add(new PersistedVolume(
                        reader.GetInt64(0),
                        info,
                        reader.IsDBNull(12) ? 0 : unchecked((ulong)reader.GetInt64(12)),
                        (VolumeSourceKind)reader.GetInt32(9),
                        (VolumeScanState)reader.GetInt32(10),
                        reader.IsDBNull(7) ? null : reader.GetInt64(7),
                        reader.IsDBNull(8) ? null : reader.GetInt64(8),
                        reader.GetInt64(11),
                        reader.IsDBNull(6) ? null : reader.GetString(6)));
                }
            }

            return list;
        }
    }

    /// <summary>Loads every persisted volume into the store. Returns the registered handles.</summary>
    public async Task<IReadOnlyList<(PersistedVolume Volume, SegmentHandle Handle)>> LoadAsync(CancellationToken ct)
    {
        var volumes = await ReadVolumesAsync(ct).ConfigureAwait(false);
        var result = new List<(PersistedVolume, SegmentHandle)>();
        foreach (var volume in volumes)
        {
            var handle = _store.FindSegment(volume.Info.RootPath) ?? await _store.RegisterVolumeAsync(volume.Info, volume.RootFileId, ct).ConfigureAwait(false);
            await LoadEntriesAsync(volume, handle, ct).ConfigureAwait(false);
            result.Add((volume, handle));
        }

        await _store.FlushAsync(ct).ConfigureAwait(false);
        // What was just loaded is already on disk.
        await _store.CapturePersistBatchesAsync(full: false, ct).ConfigureAwait(false);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    private async Task LoadEntriesAsync(PersistedVolume volume, SegmentHandle handle, CancellationToken ct)
    {
        var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT file_id, parent_id, name, attributes, size, created_utc, modified_utc FROM entries WHERE volume_id = $v AND file_id <> parent_id;";
            command.Parameters.AddWithValue("$v", volume.VolumeId);
            var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                var batch = new List<IndexDelta>(8192);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    batch.Add(IndexDelta.UpsertById(
                        unchecked((ulong)reader.GetInt64(0)),
                        unchecked((ulong)reader.GetInt64(1)),
                        reader.GetString(2),
                        unchecked((uint)reader.GetInt64(3)),
                        reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                        reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                        reader.IsDBNull(5) ? 0 : reader.GetInt64(5)));
                    if (batch.Count == 8192)
                    {
                        await _store.SubmitAsync(handle.Id, batch, ct).ConfigureAwait(false);
                        batch = new List<IndexDelta>(8192);
                    }
                }

                await _store.SubmitAsync(handle.Id, batch, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval, _clock);
        var forceFull = false;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await FlushAsync(forceFull, ct).ConfigureAwait(false);
                    forceFull = false;
                    LastError = null;
                }
                catch (SqliteException ex)
                {
                    // The captured rows were lost with the failed transaction; rewrite everything next time.
                    LastError = ex;
                    forceFull = true;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LastError = null;
        }
    }

    private static void Write(SqliteConnection connection, SqliteTransaction transaction, PersistBatch batch)
    {
        var volumeId = UpsertVolume(connection, transaction, batch);
        if (batch.Full)
        {
            using var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM entries WHERE volume_id = $v;";
            clear.Parameters.AddWithValue("$v", volumeId);
            clear.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR REPLACE INTO entries(volume_id, file_id, file_id_hi, parent_id, name, attributes, size, created_utc, modified_utc)
                VALUES ($v, $id, 0, $parent, $name, $attr, $size, $created, $modified);
                """;
            var pV = insert.Parameters.Add("$v", SqliteType.Integer);
            var pId = insert.Parameters.Add("$id", SqliteType.Integer);
            var pParent = insert.Parameters.Add("$parent", SqliteType.Integer);
            var pName = insert.Parameters.Add("$name", SqliteType.Text);
            var pAttr = insert.Parameters.Add("$attr", SqliteType.Integer);
            var pSize = insert.Parameters.Add("$size", SqliteType.Integer);
            var pCreated = insert.Parameters.Add("$created", SqliteType.Integer);
            var pModified = insert.Parameters.Add("$modified", SqliteType.Integer);
            insert.Prepare();
            pV.Value = volumeId;

            if (batch.Full)
            {
                // Root row: parent_id = file_id (§9.1).
                pId.Value = unchecked((long)batch.RootFileId);
                pParent.Value = unchecked((long)batch.RootFileId);
                pName.Value = string.Empty;
                pAttr.Value = 0x10L;
                pSize.Value = 0L;
                pCreated.Value = 0L;
                pModified.Value = 0L;
                insert.ExecuteNonQuery();
            }

            foreach (var row in batch.Upserts)
            {
                pId.Value = unchecked((long)row.FileId);
                pParent.Value = unchecked((long)row.ParentFileId);
                pName.Value = row.Name;
                pAttr.Value = (long)row.Attributes;
                pSize.Value = row.Size;
                pCreated.Value = row.CreatedUtc;
                pModified.Value = row.ModifiedUtc;
                insert.ExecuteNonQuery();
            }
        }

        if (batch.Removed.Count > 0)
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM entries WHERE volume_id = $v AND file_id_hi = 0 AND file_id = $id;";
            delete.Parameters.AddWithValue("$v", volumeId);
            var pId = delete.Parameters.Add("$id", SqliteType.Integer);
            delete.Prepare();
            foreach (var id in batch.Removed)
            {
                pId.Value = unchecked((long)id);
                delete.ExecuteNonQuery();
            }
        }

        using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "UPDATE volumes SET entry_count = $n WHERE volume_id = $v;";
        count.Parameters.AddWithValue("$n", batch.EntryCount);
        count.Parameters.AddWithValue("$v", volumeId);
        count.ExecuteNonQuery();
    }

    private static long UpsertVolume(SqliteConnection connection, SqliteTransaction transaction, PersistBatch batch)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO volumes(kind, root, volume_guid, volume_serial, fs_name)
            VALUES ($kind, $root, $guid, $serial, $fs)
            ON CONFLICT(root) DO UPDATE SET kind = excluded.kind, volume_guid = excluded.volume_guid,
                volume_serial = excluded.volume_serial, fs_name = excluded.fs_name
            RETURNING volume_id;
            """;
        command.Parameters.AddWithValue("$kind", (int)batch.Volume.Kind);
        command.Parameters.AddWithValue("$root", batch.Volume.RootPath);
        command.Parameters.AddWithValue("$guid", (object?)batch.Volume.VolumeGuid ?? DBNull.Value);
        command.Parameters.AddWithValue("$serial", unchecked((long)batch.Volume.VolumeSerial));
        command.Parameters.AddWithValue("$fs", (object?)batch.Volume.FileSystem ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
