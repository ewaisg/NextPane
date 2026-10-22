using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using NexPane.Core.Indexing;
using NexPane.Core.Vfs;
using NexPane.Storage;

namespace NexPane.Indexing.Content;

/// <summary>One <c>work_queue</c> row (§9.1).</summary>
public sealed record WorkItem(long ItemId, ContentWorkKind Kind, long? DocId, string Path, int Priority, int Attempts, long NotBeforeUtc, string? LastError);

/// <summary>
/// index.db content tables (§9.1): <c>work_queue</c>, <c>documents</c>, <c>doc_text</c> (+ FTS5 triggers). Writes go
/// through the single <see cref="SqliteWriter"/>; reads use pooled WAL reader connections. Implements
/// <see cref="IContentIndex"/> for search (<c>content:</c> terms, content regex text lookups).
/// </summary>
public sealed class ContentStore(SqliteConnectionFactory indexDb, SqliteWriter writer, TimeProvider? clock = null) : IContentIndex
{
    /// <summary>A claimed item is invisible to other workers for this long (crash safety).</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public TimeProvider Clock => _clock;

    public Task EnqueueAsync(VfsPath path, ContentWorkKind kind, int priority, CancellationToken ct) =>
        EnqueueAsync(path.Scheme == VfsPath.FileScheme ? path.ToWindowsPath() : path.ToString(), kind, priority, ct);

    /// <summary>Adds or re-prioritizes a work item (lower number = sooner; a pending item keeps the higher priority).</summary>
    public Task EnqueueAsync(string path, ContentWorkKind kind, int priority, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var now = _clock.GetUtcNow().ToFileTime();
        return writer.ExecuteAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO work_queue(kind, path, priority, enqueued_utc) VALUES ($k, $p, $prio, $now)
                ON CONFLICT(kind, path) DO UPDATE SET priority = MIN(priority, excluded.priority),
                    attempts = CASE WHEN excluded.priority < priority THEN 0 ELSE attempts END,
                    not_before_utc = CASE WHEN excluded.priority < priority THEN 0 ELSE not_before_utc END;
                """;
            cmd.Parameters.AddWithValue("$k", (int)kind);
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$prio", Math.Clamp(priority, 0, 9));
            cmd.Parameters.AddWithValue("$now", now);
            return cmd.ExecuteNonQuery();
        }, ct);
    }

    /// <summary>Claims up to <paramref name="count"/> ready items of the given kinds (priority order) by pushing their
    /// <c>not_before_utc</c> past the lease.</summary>
    public Task<IReadOnlyList<WorkItem>> ClaimAsync(IReadOnlyCollection<ContentWorkKind> kinds, int count, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        return writer.ExecuteInTransactionAsync<IReadOnlyList<WorkItem>>((c, t) =>
        {
            using var select = c.CreateCommand();
            select.Transaction = t;
            select.CommandText = $"""
                SELECT item_id, kind, doc_id, path, priority, attempts, not_before_utc, last_error FROM work_queue
                WHERE not_before_utc <= $now AND kind IN ({string.Join(',', kinds.Select(k => ((int)k).ToString(CultureInfo.InvariantCulture)))})
                ORDER BY priority, not_before_utc, item_id LIMIT $n;
                """;
            select.Parameters.AddWithValue("$now", now.ToFileTime());
            select.Parameters.AddWithValue("$n", count);
            var items = new List<WorkItem>();
            using (var reader = select.ExecuteReader())
            {
                while (reader.Read())
                {
                    items.Add(new WorkItem(reader.GetInt64(0), (ContentWorkKind)reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetInt64(2),
                        reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
                }
            }

            using var lease = c.CreateCommand();
            lease.Transaction = t;
            lease.CommandText = "UPDATE work_queue SET not_before_utc = $until WHERE item_id = $id;";
            var until = lease.Parameters.AddWithValue("$until", (now + Lease).ToFileTime());
            var id = lease.Parameters.Add("$id", SqliteType.Integer);
            foreach (var item in items)
            {
                id.Value = item.ItemId;
                lease.ExecuteNonQuery();
            }

            return items;
        }, ct);
    }

    public Task CompleteAsync(WorkItem item, CancellationToken ct) => writer.ExecuteAsync(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM work_queue WHERE item_id = $id;";
        cmd.Parameters.AddWithValue("$id", item.ItemId);
        return cmd.ExecuteNonQuery();
    }, ct);

    /// <summary>Records a failure: retried with exponential backoff (<paramref name="baseDelay"/> × 2^(attempt−1)) until
    /// <paramref name="maxAttempts"/>; then the item is dropped. Returns true when another attempt is scheduled.</summary>
    public Task<bool> FailAsync(WorkItem item, string error, TimeSpan baseDelay, int maxAttempts, CancellationToken ct)
    {
        var attempts = item.Attempts + 1;
        var retry = attempts < maxAttempts;
        var notBefore = _clock.GetUtcNow() + TimeSpan.FromTicks(baseDelay.Ticks * (1L << Math.Min(attempts - 1, 20)));
        return writer.ExecuteAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = retry
                ? "UPDATE work_queue SET attempts = $a, not_before_utc = $nb, last_error = $e WHERE item_id = $id;"
                : "DELETE FROM work_queue WHERE item_id = $id;";
            cmd.Parameters.AddWithValue("$a", attempts);
            cmd.Parameters.AddWithValue("$nb", notBefore.ToFileTime());
            cmd.Parameters.AddWithValue("$e", error);
            cmd.Parameters.AddWithValue("$id", item.ItemId);
            cmd.ExecuteNonQuery();
            return retry;
        }, ct);
    }

    public async Task<IReadOnlyList<WorkItem>> ListQueueAsync(CancellationToken ct)
    {
        var connection = await indexDb.OpenAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT item_id, kind, doc_id, path, priority, attempts, not_before_utc, last_error FROM work_queue ORDER BY priority, item_id;";
            var items = new List<WorkItem>();
            var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    items.Add(new WorkItem(reader.GetInt64(0), (ContentWorkKind)reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetInt64(2),
                        reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
                }
            }

            return items;
        }
    }

    /// <summary>Stored (size, modified) of a document, for change detection.</summary>
    public async Task<(long Size, long ModifiedUtc, ExtractStatus Status)?> GetDocumentStampAsync(string path, CancellationToken ct)
    {
        var connection = await indexDb.OpenAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT size, modified_utc, extract_status FROM documents WHERE path = $p ORDER BY indexed_utc DESC LIMIT 1;";
            cmd.Parameters.AddWithValue("$p", path);
            var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                return await reader.ReadAsync(ct).ConfigureAwait(false)
                    ? (reader.GetInt64(0), reader.GetInt64(1), (ExtractStatus)reader.GetInt32(2))
                    : null;
            }
        }
    }

    /// <summary>Writes a document row and its text of <paramref name="kind"/> (FTS kept in sync by triggers).</summary>
    public Task<long> SaveDocumentAsync(DocumentRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        var now = _clock.GetUtcNow().ToFileTime();
        return writer.ExecuteInTransactionAsync((c, t) =>
        {
            using var volume = c.CreateCommand();
            volume.Transaction = t;
            volume.CommandText = """
                INSERT INTO volumes(kind, root, volume_serial) VALUES ($kind, $root, $serial) ON CONFLICT(root) DO NOTHING;
                SELECT volume_id FROM volumes WHERE root = $root;
                """;
            volume.Parameters.AddWithValue("$kind", (int)record.VolumeKind);
            volume.Parameters.AddWithValue("$root", record.VolumeRoot);
            volume.Parameters.AddWithValue("$serial", unchecked((long)record.VolumeSerial));
            var volumeId = Convert.ToInt64(volume.ExecuteScalar(), CultureInfo.InvariantCulture);

            using var doc = c.CreateCommand();
            doc.Transaction = t;
            doc.CommandText = """
                INSERT INTO documents(volume_id, file_id, file_id_hi, path, size, modified_utc, mime, extractor, extract_status, extract_error, indexed_utc)
                VALUES ($v, $id, $hi, $path, $size, $mod, NULL, $extractor, $status, $error, $now)
                ON CONFLICT(volume_id, file_id_hi, file_id) DO UPDATE SET path = excluded.path, size = excluded.size,
                    modified_utc = excluded.modified_utc, extractor = excluded.extractor, extract_status = excluded.extract_status,
                    extract_error = excluded.extract_error, indexed_utc = excluded.indexed_utc
                RETURNING doc_id;
                """;
            doc.Parameters.AddWithValue("$v", volumeId);
            doc.Parameters.AddWithValue("$id", unchecked((long)record.FileId));
            doc.Parameters.AddWithValue("$hi", unchecked((long)record.FileIdHigh));
            doc.Parameters.AddWithValue("$path", record.Path);
            doc.Parameters.AddWithValue("$size", record.Size);
            doc.Parameters.AddWithValue("$mod", record.ModifiedUtc);
            doc.Parameters.AddWithValue("$extractor", record.Extractor);
            doc.Parameters.AddWithValue("$status", (int)record.Status);
            doc.Parameters.AddWithValue("$error", (object?)record.Error ?? DBNull.Value);
            doc.Parameters.AddWithValue("$now", now);
            var docId = Convert.ToInt64(doc.ExecuteScalar(), CultureInfo.InvariantCulture);

            using var text = c.CreateCommand();
            text.Transaction = t;
            if (record.Text is null)
            {
                text.CommandText = "DELETE FROM doc_text WHERE doc_id = $d AND kind = $k;";
            }
            else
            {
                text.CommandText = """
                    INSERT INTO doc_text(doc_id, kind, lang, producer, text, created_utc) VALUES ($d, $k, NULL, $producer, $text, $now)
                    ON CONFLICT(doc_id, kind) DO UPDATE SET text = excluded.text, producer = excluded.producer, created_utc = excluded.created_utc;
                    """;
                text.Parameters.AddWithValue("$producer", record.Extractor);
                text.Parameters.AddWithValue("$text", record.Text);
                text.Parameters.AddWithValue("$now", now);
            }

            text.Parameters.AddWithValue("$d", docId);
            text.Parameters.AddWithValue("$k", (int)record.TextKind);
            text.ExecuteNonQuery();
            return docId;
        }, ct);
    }

    /// <summary>Removes a document (cascades to its text, FTS rows and queue items).</summary>
    public Task<int> RemoveDocumentAsync(string path, CancellationToken ct) => writer.ExecuteAsync(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM documents WHERE path = $p; DELETE FROM work_queue WHERE path = $p;";
        cmd.Parameters.AddWithValue("$p", path);
        return cmd.ExecuteNonQuery();
    }, ct);

    /// <summary>FTS5 query over all extracted text (§6.2), best rank first, one hit per document.</summary>
    public async IAsyncEnumerable<ContentHit> FullTextAsync(string ftsQuery, int limit, [EnumeratorCancellation] CancellationToken ct)
    {
        var connection = await indexDb.OpenAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT d.doc_id, d.path, MIN(m.score) AS score
                FROM (SELECT rowid AS text_id, rank AS score FROM doc_text_fts WHERE doc_text_fts MATCH $q ORDER BY rank LIMIT $inner) m
                JOIN doc_text t ON t.text_id = m.text_id JOIN documents d ON d.doc_id = t.doc_id
                GROUP BY d.doc_id ORDER BY score LIMIT $n;
                """;
            cmd.Parameters.AddWithValue("$q", ftsQuery);
            cmd.Parameters.AddWithValue("$n", limit);
            cmd.Parameters.AddWithValue("$inner", (long)limit * 5);
            var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    yield return new ContentHit(reader.GetInt64(0), reader.GetString(1), -reader.GetDouble(2));
                }
            }
        }
    }

    public async Task<string?> GetTextAsync(VfsPath path, DocTextKind kind, CancellationToken ct)
    {
        var connection = await indexDb.OpenAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT t.text FROM doc_text t JOIN documents d ON d.doc_id = t.doc_id WHERE d.path = $p AND t.kind = $k LIMIT 1;";
            cmd.Parameters.AddWithValue("$p", path.Scheme == VfsPath.FileScheme ? path.ToWindowsPath() : path.ToString());
            cmd.Parameters.AddWithValue("$k", (int)kind);
            return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }
    }
}

/// <summary>Values written for one extracted document.</summary>
public sealed record DocumentRecord(
    string Path,
    string VolumeRoot,
    VolumeKind VolumeKind,
    ulong VolumeSerial,
    ulong FileId,
    ulong FileIdHigh,
    long Size,
    long ModifiedUtc,
    string Extractor,
    ExtractStatus Status,
    string? Text,
    string? Error = null,
    DocTextKind TextKind = DocTextKind.Content);
